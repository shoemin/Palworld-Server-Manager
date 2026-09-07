using System.Globalization;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;

namespace PalworldServerManager.Host.Persistence;

public sealed record AuthorizationSnapshot(long Revision, AuthorizationPolicy Policy,
    IReadOnlyList<HostCapabilityGrant> HostGrants, IReadOnlyList<ServerCapabilityGrant> ServerGrants);

// Trusted Host-only seam. Callers retain the machine lease; this is neither authentication
// nor a permission-inspection RPC. Writes never reuse a caller's previous snapshot.
public sealed partial class GrantPolicyRepository
{
    private readonly HostDatabase database;
    private readonly Guid hostId;
    private readonly TimeProvider time;
    public GrantPolicyRepository(HostDatabase database,Guid hostId,TimeProvider? timeProvider=null)
    {
        this.database=database??throw new ArgumentNullException(nameof(database));
        this.hostId=hostId!=Guid.Empty?hostId:throw new ArgumentException("Host identity required.");
        time=timeProvider??TimeProvider.System;
    }
    private SqliteConnection Open(bool readOnly=false)
    {
        var c=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=database.DatabasePath,
            Mode=readOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWrite,Pooling=false,ForeignKeys=true}.ToString());
        try{c.Open();return c;}catch{c.Dispose();throw;}
    }
    private static SqliteCommand Command(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object? Value)[] values)
    {
        var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;
        foreach(var (name,value) in values)cmd.Parameters.AddWithValue(name,value??DBNull.Value);return cmd;
    }
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Name,object? Value)[] values)
    {using var cmd=Command(c,tx,sql,values);cmd.ExecuteNonQuery();}
    private static string Id(Guid id)=>id!=Guid.Empty?id.ToString("D"):throw new ArgumentException("Identity required.");
    private static Guid ParseId(string text)=>Guid.TryParseExact(text,"D",out var id)&&id!=Guid.Empty?id:throw Corrupt();
    private static InvalidDataException Corrupt()=>new("Invalid persisted authorization state.");
    private static string Stamp(DateTimeOffset utc)=>utc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string text)=>DateTimeOffset.ParseExact(text,"O",CultureInfo.InvariantCulture);
    private static T Capability<T>(string text)where T:struct,Enum
        =>Enum.TryParse<T>(text,out var value)&&Enum.IsDefined(value)&&value.ToString()==text?value:throw Corrupt();
    private static ActorRef Actor(SqliteDataReader r,int kind,int local,int peer)=>r.GetString(kind) switch
    {
        "LocalPrincipal" when !r.IsDBNull(local)&&r.IsDBNull(peer)=>ActorRef.LocalPrincipal(ParseId(r.GetString(local))),
        "RemoteManager" when r.IsDBNull(local)&&!r.IsDBNull(peer)=>ActorRef.RemoteManager(ParseId(r.GetString(peer))),
        _=>throw Corrupt()
    };
    private static long Revision(SqliteConnection c,SqliteTransaction tx)
    {
        using var cmd=Command(c,tx,"SELECT Revision FROM AuthorizationRevision WHERE Id=1 AND typeof(Revision)='integer' AND Revision>=0;");
        return cmd.ExecuteScalar() is long value?value:throw Corrupt();
    }
    public AuthorizationSnapshot Read()
    {using var c=Open(true);using var tx=c.BeginTransaction(deferred:true);return Read(c,tx);}
    private AuthorizationSnapshot Read(SqliteConnection c,SqliteTransaction tx)
    {
        var revision=Revision(c,tx);bool initialized;
        using(var cmd=Command(c,tx,"SELECT HostId,HostBootstrapState FROM HostIdentity WHERE Id=1;"))
        {
            using var r=cmd.ExecuteReader();
            if(!r.Read()||ParseId(r.GetString(0))!=hostId)throw Corrupt();
            initialized=r.GetString(1) switch {"Initialized"=>true,"Uninitialized"=>false,_=>throw Corrupt()};
        }
        var locals=new List<Guid>();var owners=new List<Guid>();var peers=new List<Guid>();
        using(var cmd=Command(c,tx,"SELECT LocalPrincipalId,IsOwner FROM LocalPrincipals WHERE State='Active';"))
        {using var r=cmd.ExecuteReader();while(r.Read()){var id=ParseId(r.GetString(0));locals.Add(id);if(r.GetInt32(1)==1)owners.Add(id);}}
        if(owners.Count!=(initialized?1:0))throw Corrupt();
        using(var cmd=Command(c,tx,"SELECT PeerHostId FROM TrustedManagers WHERE State='Active' AND PeerRecoveryRequired=0;"))
        {using var r=cmd.ExecuteReader();while(r.Read())peers.Add(ParseId(r.GetString(0)));}
        var hs=new List<HostCapabilityGrant>();var ss=new List<ServerCapabilityGrant>();
        foreach(var server in new[]{false,true})
        {
            var table=server?"ServerCapabilityGrants":"HostCapabilityGrants";
            var target=server?"AuthoritativeHostId,ServerProfileId":"TargetHostId,NULL";
            using var cmd=Command(c,tx,$"""
                SELECT GrantId,GranteeActorKind,GranteeLocalPrincipalId,GranteePeerHostId,
                    GrantedByActorKind,GrantedByLocalPrincipalId,GrantedByPeerHostId,
                    CanDelegate,CanDelegateOnwardDelegation,DerivedFromGrantId,CreatedUtc,InvalidatedUtc,
                    Capability,{target} FROM {table};
                """);
            using var r=cmd.ExecuteReader();
            while(r.Read())
            {
                var id=ParseId(r.GetString(0));var grantee=Actor(r,1,2,3);var grantor=Actor(r,4,5,6);
                var rights=new DelegationRights(r.GetInt32(7)==1,r.GetInt32(8)==1);
                Guid? parent=r.IsDBNull(9)?null:ParseId(r.GetString(9));var created=ParseTime(r.GetString(10));
                DateTimeOffset? invalid=r.IsDBNull(11)?null:ParseTime(r.GetString(11));
                if(server)ss.Add(new(id,grantee,Capability<ServerCapability>(r.GetString(12)),new(ParseId(r.GetString(13)),ParseId(r.GetString(14))),rights,grantor,parent,created,invalid));
                else hs.Add(new(id,grantee,Capability<HostCapability>(r.GetString(12)),ParseId(r.GetString(13)),rights,grantor,parent,created,invalid));
            }
        }
        return new(revision,new(hostId,initialized?owners[0]:null,locals,peers,hs,ss),hs.AsReadOnly(),ss.AsReadOnly());
    }
}

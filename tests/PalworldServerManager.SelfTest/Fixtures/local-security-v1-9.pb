
Õ
Protos/local_security.protopalworld.manager.v1Protos/host.protoProtos/peer_pairing.protoProtos/peer_security.proto"

LocalEmpty"¬
LocalHandshakeReply<
	handshake (2.palworld.manager.v1.HandshakeR	handshake5
host (2!.palworld.manager.v1.HostIdentityRhost 
initialized (Rinitialized"E
LocalPrincipalRequest,
local_principal_id (	RlocalPrincipalId"*
LocalChallenge
payload (Rpayload"*

LocalProof
	signature (R	signature"a
LocalPrincipalIdentity,
local_principal_id (	RlocalPrincipalId
is_owner (RisOwner"K
LocalEnrollmentTarget2
intended_os_principal (	RintendedOsPrincipal"m
LocalEnrollmentInvitation
	ticket_id (	RticketId
expires_utc (	R
expiresUtc
code (Rcode"o
LocalCredentialCompletion
	ticket_id (	RticketId
secret (Rsecret

public_key (R	publicKey"E
LocalCredentialResult,
local_principal_id (	RlocalPrincipalId"r
LocalPairingInvitation#
invitation_id (	RinvitationId
expires_utc (	R
expiresUtc
code (Rcode"D
LocalPairingInvitationRequest#
invitation_id (	RinvitationId"t
LocalPairHostRequest%
reachable_host (	RreachableHost!
pairing_port (RpairingPort
code (Rcode"½
LocalPairHostReply1
verified_peer_host_id (	RverifiedPeerHostIdI
local_result (2&.palworld.manager.v1.PeerPairingResultRlocalResultK
remote_result (2&.palworld.manager.v1.PeerPairingResultRremoteResult0
local_replacement_id (	RlocalReplacementId*
local_expires_utc (	RlocalExpiresUtc"L
LocalPairingDiscoveryRequest
offset (Roffset
limit (Rlimit"‚
LocalDiscoveredPairingHost&
claimed_host_id (	RclaimedHostId%
reachable_host (	RreachableHost
	peer_port (RpeerPort!
pairing_port (RpairingPortU
advertised_protocol (2$.palworld.manager.v1.ProtocolVersionRadvertisedProtocol"„
LocalPairingDiscoveryReplyE
hosts (2/.palworld.manager.v1.LocalDiscoveredPairingHostRhosts
next_offset (R
nextOffset"€
LocalActivatePeerRequest 
peer_host_id (	R
peerHostId%
reachable_host (	RreachableHost
	peer_port (RpeerPort"[
LocalActivatePeerReplyA
result (2).palworld.manager.v1.PeerActivationResultRresult2È
LocalSecurityProtocolU
	Negotiate.palworld.manager.v1.Handshake(.palworld.manager.v1.LocalHandshakeReplya
IssueChallenge*.palworld.manager.v1.LocalPrincipalRequest#.palworld.manager.v1.LocalChallenge\
Authenticate.palworld.manager.v1.LocalProof+.palworld.manager.v1.LocalPrincipalIdentity[
GetIdentity.palworld.manager.v1.LocalEmpty+.palworld.manager.v1.LocalPrincipalIdentityn
CreateEnrollment*.palworld.manager.v1.LocalEnrollmentTarget..palworld.manager.v1.LocalEnrollmentInvitation^
RevokePrincipal*.palworld.manager.v1.LocalPrincipalRequest.palworld.manager.v1.LocalEmptyo
CompleteBootstrap..palworld.manager.v1.LocalCredentialCompletion*.palworld.manager.v1.LocalCredentialResultp
CompleteEnrollment..palworld.manager.v1.LocalCredentialCompletion*.palworld.manager.v1.LocalCredentialResults
CompleteOwnerRotation..palworld.manager.v1.LocalCredentialCompletion*.palworld.manager.v1.LocalCredentialResultq
CompleteOwnerRehome..palworld.manager.v1.LocalCredentialCompletion*.palworld.manager.v1.LocalCredentialResultg
CreatePairingInvitation.palworld.manager.v1.LocalEmpty+.palworld.manager.v1.LocalPairingInvitationn
CancelPairingInvitation2.palworld.manager.v1.LocalPairingInvitationRequest.palworld.manager.v1.LocalEmptyz
DiscoverPairingHosts1.palworld.manager.v1.LocalPairingDiscoveryRequest/.palworld.manager.v1.LocalPairingDiscoveryReply^
PairHost).palworld.manager.v1.LocalPairHostRequest'.palworld.manager.v1.LocalPairHostReplyj
ActivatePeer-.palworld.manager.v1.LocalActivatePeerRequest+.palworld.manager.v1.LocalActivatePeerReplyB'ª$PalworldServerManager.Contracts.Wirebproto3
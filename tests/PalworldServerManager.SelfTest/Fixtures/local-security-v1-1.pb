
Ó
Protos/local_security.protopalworld.manager.v1Protos/host.proto"

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
local_principal_id (	RlocalPrincipalId2§
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
CompleteOwnerRehome..palworld.manager.v1.LocalCredentialCompletion*.palworld.manager.v1.LocalCredentialResultB'ª$PalworldServerManager.Contracts.Wirebproto3
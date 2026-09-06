//! Host-only RFC 9382 adapter. ABI callers must provide valid, nonoverlapping buffers.
//! No pointers/secret keys are returned. Failed operations remove the opaque handle.
use hmac::{Hmac, Mac};
use pakery_core::crypto::CpaceGroup;
use pakery_crypto::{P256Group, Spake2P256};
use pakery_spake2::{PartyA, PartyAState, PartyB, PartyBState, Spake2Output};
use rand_core::SeedableRng;
use sha2::Sha256;
use std::{
    collections::HashMap,
    sync::{
        atomic::{AtomicU64, Ordering},
        Mutex, OnceLock,
    },
};
use zeroize::Zeroizing;

enum Phase {
    A(PartyAState<Spake2P256>),
    B(PartyBState<Spake2P256>),
    Pending(Spake2Output),
    Confirmed(Zeroizing<Vec<u8>>),
}
struct Session {
    role: u8,
    phase: Phase,
    own_binding: Option<Vec<u8>>,
    peer_binding: Option<Vec<u8>>,
}
static SESSIONS: OnceLock<Mutex<HashMap<u64, Session>>> = OnceLock::new();
static NEXT: AtomicU64 = AtomicU64::new(1);
const CONTEXT: &[u8] = b"PalworldManager/SPAKE2-P256-v1";
const LIMIT: usize = 128;
fn sessions() -> &'static Mutex<HashMap<u64, Session>> {
    SESSIONS.get_or_init(|| Mutex::new(HashMap::new()))
}

fn seed() -> Result<Zeroizing<[u8; 32]>, ()> {
    let mut bytes = Zeroizing::new([0u8; 32]);
    if cfg!(feature = "qualification-entropy-failure") {
        return Err(());
    }
    getrandom::fill(&mut *bytes).map_err(|_| ())?;
    Ok(bytes)
}

fn create(role: u8, code: &[u8], nonce: &[u8]) -> Result<(Session, Vec<u8>), ()> {
    if role > 1 || code.len() != 10 || !code.iter().all(u8::is_ascii_digit) || nonce.len() != 32 {
        return Err(());
    }
    let entropy = seed()?;
    let mut rng = rand_chacha::ChaCha20Rng::from_seed(*entropy);
    let params = argon2::Params::new(19456, 2, 1, Some(64)).map_err(|_| ())?;
    let mut wide = Zeroizing::new([0u8; 64]);
    argon2::Argon2::new(argon2::Algorithm::Argon2id, argon2::Version::V0x13, params)
        .hash_password_into(code, nonce, &mut *wide)
        .map_err(|_| ())?;
    let w = Zeroizing::new(P256Group::scalar_from_wide_bytes(&*wide).map_err(|_| ())?);
    let mut context = Vec::from(CONTEXT);
    context.extend_from_slice(nonce);
    let (share, phase) = if role == 0 {
        let (share, state) =
            PartyA::<Spake2P256>::start(&w, b"initiator", b"responder", &context, &mut rng)
                .map_err(|_| ())?;
        (share, Phase::A(state))
    } else {
        let (share, state) =
            PartyB::<Spake2P256>::start(&w, b"initiator", b"responder", &context, &mut rng)
                .map_err(|_| ())?;
        (share, Phase::B(state))
    };
    Ok((
        Session {
            role,
            phase,
            own_binding: None,
            peer_binding: None,
        },
        share,
    ))
}

// Return bytes only on successful operation; errors are terminal even on wrong phase/length.
fn operate(session: &mut Session, op: u32, input: &[u8]) -> Result<Vec<u8>, ()> {
    match op {
        0 => {
            if input.len() != 65 || input[0] != 4 {
                return Err(());
            }
            let phase = std::mem::replace(
                &mut session.phase,
                Phase::Confirmed(Zeroizing::new(Vec::new())),
            );
            let output = match phase {
                Phase::A(a) => a.finish(input),
                Phase::B(b) => b.finish(input),
                _ => return Err(()),
            }
            .map_err(|_| ())?;
            let confirmation = if session.role == 0 {
                output.confirmation_mac.clone()
            } else {
                Vec::new()
            };
            session.phase = Phase::Pending(output);
            Ok(confirmation)
        }
        1 => {
            if input.len() != 32 {
                return Err(());
            }
            let phase = std::mem::replace(
                &mut session.phase,
                Phase::Confirmed(Zeroizing::new(Vec::new())),
            );
            let Phase::Pending(output) = phase else {
                return Err(());
            };
            output.verify_peer_confirmation(input).map_err(|_| ())?;
            let confirmation = if session.role == 1 {
                output.confirmation_mac.clone()
            } else {
                Vec::new()
            };
            // Ke is never exported. Derive a separate application binding key after confirmation.
            let mut key = Zeroizing::new(vec![0u8; 32]);
            hkdf::Hkdf::<Sha256>::new(None, output.session_key.as_bytes())
                .expand(b"PalworldManager/HostIdentityBinding/v1", &mut key)
                .map_err(|_| ())?;
            session.phase = Phase::Confirmed(key);
            Ok(confirmation)
        }
        2 | 3 => {
            // Local identity must be fixed before any peer identity can be returned.
            if op == 3 && session.own_binding.is_none() {
                return Err(());
            }
            let Phase::Confirmed(key) = &session.phase else {
                return Err(());
            };
            if key.len() != 32 {
                return Err(());
            }
            let (payload, tag) = if op == 3 {
                if input.len() < 32 {
                    return Err(());
                }
                (&input[..input.len() - 32], &input[input.len() - 32..])
            } else {
                (input, &[][..])
            };
            if payload.len() < 21 || payload.len() > 1044 {
                return Err(());
            }
            let length = u32::from_be_bytes(payload[16..20].try_into().map_err(|_| ())?) as usize;
            if length != payload.len() - 20 || payload[..16].iter().all(|v| *v == 0) {
                return Err(());
            }
            if op == 3
                && session
                    .own_binding
                    .as_ref()
                    .is_some_and(|own| own[..16] == payload[..16])
            {
                return Err(());
            }
            let role = if op == 2 {
                session.role
            } else {
                1 - session.role
            };
            let mut mac = Hmac::<Sha256>::new_from_slice(key).map_err(|_| ())?;
            mac.update(b"PalworldManager/BindingMessage/v1");
            mac.update(&[role]);
            mac.update(payload);
            if op == 3 {
                mac.verify_slice(tag).map_err(|_| ())?;
            } else {
                if session
                    .own_binding
                    .as_deref()
                    .is_some_and(|previous| previous != payload)
                {
                    return Err(());
                }
                session.own_binding = Some(payload.to_vec());
                return Ok(mac.finalize().into_bytes().to_vec());
            }
            if session
                .peer_binding
                .as_deref()
                .is_some_and(|previous| previous != payload)
            {
                return Err(());
            }
            session.peer_binding = Some(payload.to_vec());
            Ok(Vec::new())
        }
        _ => Err(()),
    }
}

/// ABI v1; the high bit distinguishes an entropy-failure qualification build.
#[no_mangle]
pub extern "C" fn psm_pake_abi() -> u32 {
    if cfg!(feature = "qualification-entropy-failure") {
        0x80000001
    } else {
        1
    }
}

#[no_mangle]
pub unsafe extern "C" fn psm_pake_create(
    role: u8,
    code: *const u8,
    code_len: usize,
    nonce: *const u8,
    nonce_len: usize,
    share: *mut u8,
    share_len: usize,
) -> u64 {
    std::panic::catch_unwind(|| {
        if code.is_null()
            || nonce.is_null()
            || share.is_null()
            || code_len != 10
            || nonce_len != 32
            || share_len != 65
        {
            return 0;
        }
        // Serialize creations as well as operations; bound memory-hard work and resident sessions.
        let mut map = sessions()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if map.len() >= LIMIT {
            return 0;
        }
        let Ok((state, bytes)) = create(
            role,
            std::slice::from_raw_parts(code, code_len),
            std::slice::from_raw_parts(nonce, nonce_len),
        ) else {
            return 0;
        };
        let Ok(id) = NEXT.fetch_update(Ordering::Relaxed, Ordering::Relaxed, |n| n.checked_add(1))
        else {
            return 0;
        };
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), share, 65);
        map.insert(id, state);
        id
    })
    .unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn psm_pake_step(
    id: u64,
    op: u32,
    input: *const u8,
    input_len: usize,
    output: *mut u8,
    output_len: usize,
) -> i32 {
    std::panic::catch_unwind(|| {
        let mut map = sessions()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        // Ownership leaves the registry first. Any error/panic drops the state permanently.
        let Some(mut session) = map.remove(&id) else {
            return 2;
        };
        let required = match op {
            0 => {
                if session.role == 0 {
                    32
                } else {
                    0
                }
            }
            1 => {
                if session.role == 1 {
                    32
                } else {
                    0
                }
            }
            2 => 32,
            3 => 0,
            _ => return 1,
        };
        if input.is_null()
            || input_len > 1076
            || output_len != required
            || (required > 0 && output.is_null())
        {
            return 1;
        }
        let Ok(bytes) = operate(
            &mut session,
            op,
            std::slice::from_raw_parts(input, input_len),
        ) else {
            return 1;
        };
        if bytes.len() != required {
            return 3;
        }
        if required > 0 {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), output, required);
        }
        map.insert(id, session);
        0
    })
    .unwrap_or(3)
}

#[no_mangle]
pub extern "C" fn psm_pake_close(id: u64) {
    sessions()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .remove(&id);
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[cfg(feature = "qualification-entropy-failure")]
    fn entropy_failure_is_explicit() {
        assert!(create(0, b"1234567890", &[1; 32]).is_err());
    }
    #[test]
    #[cfg(not(feature = "qualification-entropy-failure"))]
    fn failed_native_handle_cannot_be_reused() {
        unsafe {
            let mut share = [0u8; 65];
            let id = psm_pake_create(
                0,
                b"1234567890".as_ptr(),
                10,
                [1u8; 32].as_ptr(),
                32,
                share.as_mut_ptr(),
                65,
            );
            assert_ne!(id, 0);
            assert_ne!(
                psm_pake_step(id, 1, [0u8; 32].as_ptr(), 32, std::ptr::null_mut(), 0),
                0
            );
            let mut mac = [0u8; 32];
            assert_eq!(
                psm_pake_step(id, 0, share.as_ptr(), 65, mac.as_mut_ptr(), 32),
                2
            );
            psm_pake_close(id);
            psm_pake_close(id);
        }
    }
}

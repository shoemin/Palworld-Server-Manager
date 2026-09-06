//! Isolated assessment fixtures. Never link this deterministic test library into a Host.
//! Integer-only export proves Windows/.NET loading, not a production session ABI.
use pakery_core::crypto::CpaceGroup;
use pakery_crypto::{P256Group, Spake2P256, SPAKE2_P256_N_COMPRESSED};
use pakery_spake2::{PartyA, PartyB, Spake2Output};
use zeroize::Zeroize;

type A = PartyA<Spake2P256>;
type B = PartyB<Spake2P256>;

fn scalar(value: u8) -> <P256Group as CpaceGroup>::Scalar {
    let mut bytes = [0u8; 64];
    bytes[63] = value;
    P256Group::scalar_from_wide_bytes(&bytes).unwrap()
}

fn exchange(w_b: u8, id_b: &[u8], aad_b: &[u8], seed: u8) -> (Spake2Output, Spake2Output) {
    let (pa, a) = A::start_with_scalar(&scalar(7), &scalar(seed), b"A", b"B", b"context").unwrap();
    let (pb, b) = B::start_with_scalar(&scalar(w_b), &scalar(seed + 1), b"A", id_b, aad_b).unwrap();
    (a.finish(&pb).unwrap(), b.finish(&pa).unwrap())
}

fn refused(a: &Spake2Output, b: &Spake2Output) -> bool {
    a.verify_peer_confirmation(&b.confirmation_mac).is_err()
        && b.verify_peer_confirmation(&a.confirmation_mac).is_err()
}

fn case(id: i32) -> bool {
    match id {
        0 => {
            let (a, b) = exchange(7, b"B", b"context", 11);
            a.verify_peer_confirmation(&b.confirmation_mac).is_ok()
                && b.verify_peer_confirmation(&a.confirmation_mac).is_ok()
                && a.session_key.as_bytes() == b.session_key.as_bytes()
        }
        1 => { let (a, b) = exchange(8, b"B", b"context", 11); refused(&a, &b) }
        2 => { let (a, b) = exchange(7, b"substituted", b"context", 11); refused(&a, &b) }
        3 => { let (a, b) = exchange(7, b"B", b"other-context", 11); refused(&a, &b) }
        4 => {
            // Reject both roles' invalid/identity/noncanonical/truncated/oversized wire points.
            let invalid = [vec![], vec![0], vec![0xff; 65], vec![4; 64], vec![4; 66], vec![5; 33]];
            invalid.iter().all(|point| {
                let (_, a) = A::start_with_scalar(&scalar(7), &scalar(11), b"A", b"B", b"context").unwrap();
                let (_, b) = B::start_with_scalar(&scalar(7), &scalar(12), b"A", b"B", b"context").unwrap();
                a.finish(point).is_err() && b.finish(point).is_err()
            })
        }
        5 => {
            let (a, b) = exchange(7, b"B", b"context", 11);
            [0usize, 1, 31, 32, 33, 1024].iter().all(|length| {
                a.verify_peer_confirmation(&vec![0; *length]).is_err()
                    && b.verify_peer_confirmation(&vec![0; *length]).is_err()
            })
        }
        6 => {
            let (a, b) = exchange(7, b"B", b"context", 11);
            a.verify_peer_confirmation(&a.confirmation_mac).is_err()
                && b.verify_peer_confirmation(&b.confirmation_mac).is_err()
        }
        7 => {
            let (a, _) = exchange(7, b"B", b"context", 11);
            let (_, old_b) = exchange(7, b"B", b"context", 21);
            a.verify_peer_confirmation(&old_b.confirmation_mac).is_err()
        }
        8 => {
            let (_, a) = A::start_with_scalar(&scalar(7), &scalar(11), b"A", b"B", b"context").unwrap();
            let n = P256Group::from_bytes(&SPAKE2_P256_N_COMPRESSED).unwrap();
            a.finish(&n.scalar_mul(&scalar(7)).to_bytes()).is_err()
        }
        9 => {
            let (pa, a) = A::start_with_scalar(&scalar(7), &scalar(11), b"A", b"B", b"context").unwrap();
            let (_, b) = B::start_with_scalar(&scalar(7), &scalar(12), b"A", b"B", b"context").unwrap();
            match a.finish(&pa) {
                Err(_) => true,
                Ok(reflected) => reflected.verify_peer_confirmation(&b.finish(&pa).unwrap().confirmation_mac).is_err(),
            }
        }
        // These three cases reproduce API hazards, not successful security gates.
        10 => {
            let (mut a, _) = exchange(7, b"B", b"context", 11);
            a.zeroize();
            a.verify_peer_confirmation(&[]).is_ok()
        }
        11 => {
            let (a, _) = exchange(7, b"B", b"context", 11);
            a.into_session_key().as_bytes().len() == 16
        }
        12 => {
            let (a, b) = exchange(7, b"B", b"context", 11);
            a.verify_peer_confirmation(&[0; 32]).is_err()
                && a.verify_peer_confirmation(&b.confirmation_mac).is_ok()
        }
        _ => false,
    }
}

/// 1 = expected behavior reproduced; 0 = mismatch; -1 = caught Rust panic.
/// No pointer, allocation, session handle, private Host credential or key crosses this ABI.
#[no_mangle]
pub extern "C" fn astra_spake2_qualify(id: i32) -> i32 {
    std::panic::catch_unwind(|| i32::from(case(id))).unwrap_or(-1)
}

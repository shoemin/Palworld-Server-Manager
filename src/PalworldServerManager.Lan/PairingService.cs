using System.Security.Cryptography;

namespace PalworldServerManager.Lan;

public sealed class PairingService
{
    private readonly object _sync = new();
    private string? _code;
    private DateTime _expiresUtc;
    private int _failedAttempts;

    public (string Code, DateTime ExpiresUtc) GenerateCode()
    {
        lock (_sync)
        {
            _code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("000000");
            _expiresUtc = DateTime.UtcNow.AddMinutes(5);
            _failedAttempts = 0;
            return (_code, _expiresUtc);
        }
    }

    public bool Validate(string code)
    {
        lock (_sync)
        {
            if (_code is null || DateTime.UtcNow > _expiresUtc)
            {
                _code = null;
                return false;
            }

            if (!string.Equals(_code, code?.Trim(), StringComparison.Ordinal))
            {
                _failedAttempts++;
                if (_failedAttempts >= 10) _code = null;
                return false;
            }

            // Pairing codes are intentionally one-use.
            _code = null;
            _failedAttempts = 0;
            return true;
        }
    }

    public DateTime? ExpiresUtc
    {
        get
        {
            lock (_sync) return _code is null ? null : _expiresUtc;
        }
    }
}

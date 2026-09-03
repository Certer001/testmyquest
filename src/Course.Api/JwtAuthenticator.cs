using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Course.Shared;
using Microsoft.IdentityModel.Tokens;

namespace Course.Api;

public sealed class JwtAuthenticator(JwtSettings settings)
{
    public bool TryAuthenticate(string? authorizationHeader, out TrustedPrincipal principal, out string errorCode)
    {
        principal = null!;
        errorCode = "auth.invalid";

        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            return false;
        }

        JwtSecurityToken jwt;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            jwt = handler.ReadJwtToken(token);
        }
        catch
        {
            return false;
        }

        if (!TryGetStringClaim(jwt, "iss", out var issuer) ||
            !TryGetStringClaim(jwt, "aud", out var audience) ||
            !TryGetStringClaim(jwt, "sub", out var subject) ||
            !TryGetStringClaim(jwt, "consumer", out var consumer) ||
            !TryGetLongClaim(jwt, "iat", out var issuedAt) ||
            !TryGetLongClaim(jwt, "exp", out var expiresAt))
        {
            return false;
        }

        if (!TryGetScopeClaim(jwt, out var scopes))
        {
            return false;
        }

        if (!string.Equals(issuer, settings.Issuer, StringComparison.Ordinal) ||
            !string.Equals(audience, settings.Audience, StringComparison.Ordinal))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (expiresAt <= now)
        {
            return false;
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey));
            handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.Zero
            }, out _);
        }
        catch
        {
            return false;
        }

        principal = new TrustedPrincipal(subject, consumer, scopes);
        return true;
    }

    private static bool TryGetStringClaim(JwtSecurityToken jwt, string name, out string value)
    {
        value = string.Empty;
        var claim = jwt.Payload.FirstOrDefault(pair => pair.Key == name);
        if (claim.Value is not string text || text.Length == 0)
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryGetLongClaim(JwtSecurityToken jwt, string name, out long value)
    {
        value = 0;
        var claim = jwt.Payload.FirstOrDefault(pair => pair.Key == name);
        value = claim.Value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var number) => number,
            long number => number,
            int number => number,
            double number => (long)number,
            _ => long.MinValue
        };
        return value != long.MinValue;
    }

    private static bool TryGetScopeClaim(JwtSecurityToken jwt, out IReadOnlyList<string> scopes)
    {
        scopes = [];
        var claim = jwt.Payload.FirstOrDefault(pair => pair.Key == "scope");
        if (claim.Value is not string scopeValue)
        {
            return claim.Value is null || (claim.Value is JsonElement { ValueKind: JsonValueKind.String });
        }

        scopes = scopeValue.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return true;
    }
}

public sealed record TrustedPrincipal(string Subject, string Consumer, IReadOnlyList<string> Scopes);

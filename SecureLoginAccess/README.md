# SecureLoginAccess

## Overview
SecureLoginAccess is a .NET 8 Web API that modernizes authentication for legacy ASP.NET Membership data. It issues JWT access tokens and rotates hashed refresh tokens while reusing existing membership tables (`aspnet_Users`, `aspnet_Membership`, `aspnet_Roles`, `aspnet_UsersInRoles`). All DB access is ADO.NET; no Entity Framework.

## Features
- JWT bearer auth (HS256) with issuer/audience configured via `appsettings.json`.
- Login with legacy membership passwords (clear or SHA1 salted) and lockout after 5 failed attempts.
- Refresh-token rotation with server-stored hashes; logout revokes the refresh token.
- Role claims included in access tokens.
- Basic audit logging of login, refresh, logout events to `Auth_AuditLog`.
- Swagger UI enabled in Development.

## Stack
- .NET 8, ASP.NET Core Web API
- Microsoft.Data.SqlClient (ADO.NET)
- Swashbuckle (Swagger)

## Project structure
- `Program.cs` - DI, JWT auth, Swagger, pipeline, sample weather endpoint.
- `Controllers/AuthController.cs` - login/refresh/logout/userinfo endpoints.
- `Services/TokenService.cs` - access/refresh token generation.
- `Data/AuthService.cs` - auth logic, password verification, lockout, token rotation, auditing.
- `Data/AuthRepository.cs` - ADO.NET queries/commands for users, roles, refresh tokens, audits.
- `Models/*` - POCOs for legacy membership schema and custom tables.
- `appsettings*.json` - configuration (DB, JWT, logging).
- `Dockerfile`, `.dockerignore` - container build assets.

## Configuration
Edit `appsettings.json` (or env vars / user secrets) for production-safe values:
```json
"ConnectionStrings": {
  "DefaultConnection": "Server=...;Database=...;User ID=...;Password=...;TrustServerCertificate=True;"
},
"Jwt": {
  "Issuer": "SecureLoginAccess",
  "Audience": "SecureLoginAccessAudience",
  "Secret": "<base64-256-bit-or-32+char-secret>",
  "AccessTokenMinutes": "15"
}
```
Notes:
- `Jwt:Secret` must decode/provide >= 32 bytes; Base64 is preferred.
- Move secrets out of source control (user secrets, env vars, vault).
- Env var overrides use double underscore: `ConnectionStrings__DefaultConnection`, `Jwt__Issuer`, `Jwt__Audience`, `Jwt__Secret`, `Jwt__AccessTokenMinutes`.

## Database expectations
Legacy tables (existing):
- `dbo.aspnet_Users` (UserId PK, UserName, LoweredUserName, LastActivityDate,...)
- `dbo.aspnet_Membership` (UserId PK/FK to Users; Password, PasswordSalt, PasswordFormat, lockout counters,...)
- `dbo.aspnet_Roles`
- `dbo.aspnet_UsersInRoles`

Custom tables (expected to exist):
- `dbo.Auth_RefreshTokens` (Id PK, UserId, TokenHash, CreatedAt, ExpiresAt, CreatedByIp, RevokedAt, RevokedByIp, ReplacedByToken)
- `dbo.Auth_AuditLog` (Id PK, UserId nullable, EventType, Details, OccurredAt, IpAddress)

## Running locally
```powershell
# from repo root
 dotnet restore
 dotnet run
# Swagger UI in Development: https://localhost:5001/swagger
```

## Docker
```powershell
# Build image
 docker build -t secure-login-access .

# Run container (bind to 7012 and pass secrets via env vars)
 docker run -p 7012:7012 ^
   -e ConnectionStrings__DefaultConnection="Server=...;Database=...;User ID=...;Password=...;TrustServerCertificate=True;" ^
   -e Jwt__Issuer="SecureLoginAccess" ^
   -e Jwt__Audience="SecureLoginAccessAudience" ^
   -e Jwt__Secret="<base64-256-bit-or-32+char-secret>" ^
   secure-login-access

# App listens on http://localhost:7012
```
Notes:
- Container uses port 7012 via `ASPNETCORE_URLS=http://+:7012`.
- Use secrets management for real deployments (Docker secrets, env files excluded by `.dockerignore`).

## Authentication flow
- **Login**: verify legacy password, reset counters, update last login, issue JWT access token (default 15m) and random 64-byte refresh token (raw returned, hash stored). Lockout triggers after 5 failed attempts.
- **Refresh**: hash incoming refresh token, validate active token, rotate by revoking old record and inserting a new one, issue new access+refresh pair.
- **Logout**: revoke the provided refresh token.
- **Roles**: pulled from `aspnet_Roles` via `aspnet_UsersInRoles` and emitted as `role` claims in access tokens.

## Password handling (legacy)
- Supports `PasswordFormat` values: `Clear` (plain text) and `Hashed` (Base64 SHA1 over `salt || UTF-16(password)`).
- `Encrypted` is not implemented; requires legacy machineKey to support reversible decryption.

## API endpoints
- `POST /api/auth/login` - body `{ "emailOrUserName": "...", "password": "..." }` -> `{ accessToken, refreshToken, expiresIn }`.
- `POST /api/auth/refresh` - body `{ "refreshToken": "..." }` (or `refresh_token` cookie) -> rotated tokens.
- `POST /api/auth/logout` - body `{ "refreshToken": "..." }` to revoke.
- `GET /api/auth/userinfo` - requires `Authorization: Bearer <accessToken>`; returns `{ userId, userName, roles }`.
- `GET /weatherforecast` - sample endpoint.

## Security notes
- Refresh rotation happens with revoke+insert; ensure both operations occur within the same transaction if you tighten reuse detection.
- Consider shorter access token lifetime and longer refresh lifetime per your policy.
- Always serve over HTTPS; enable HttpOnly/SameSite cookies if you decide to store refresh tokens in cookies (code is scaffolded, commented out).
- Replace hard-coded secrets/connection strings before deployment.

## Development tips
- Set `ASPNETCORE_ENVIRONMENT=Development` to use Swagger.
- Adjust access token lifetime via `Jwt:AccessTokenMinutes`.
- Use Postman/REST Client with `SecureLoginAccess.http` for quick calls.
- Add tests around `AuthService` for lockout/rotation if you extend behavior.

## Troubleshooting
- "Jwt:Secret must provide at least 32 bytes" -> supply a longer Base64 secret.
- Login fails for `Encrypted` passwords -> provide machineKey details and implement decryption, or migrate users to hashed passwords.
- Refresh returns 401 -> ensure token not expired/revoked and hash matches stored record.

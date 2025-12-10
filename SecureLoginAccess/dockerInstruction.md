# Docker Instructions for SecureLoginAccess

## Prerequisites
- Docker installed and running.
- Network access to the SQL Server defined by `ConnectionStrings__DefaultConnection`.
- A strong JWT secret that provides at least 32 bytes (Base64 preferred).

## Build the image
```powershell
cd C:\Users\MNgalo\source\repos\SecureLoginAccess
docker build -t secure-login-access .
```

## Run the container (HTTP on 7012)
```powershell
docker run -p 7012:7012 ^
  -e ConnectionStrings__DefaultConnection="Server=...;Database=...;User ID=...;Password=...;TrustServerCertificate=True;" ^
  -e Jwt__Issuer="SecureLoginAccess" ^
  -e Jwt__Audience="SecureLoginAccessAudience" ^
  -e Jwt__Secret="<base64-256-bit-or-32+char-secret>" ^
  secure-login-access
```
- App listens on `http://localhost:7012`.
- `Jwt__Secret` must decode/provide >= 32 bytes; Base64 string is recommended. Generate one in PowerShell:
  ```powershell
  $bytes = New-Object byte[] 32
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  [Convert]::ToBase64String($bytes)
  ```

## Swagger
- In Development environment, Swagger UI is at `http://localhost:7012/swagger`.
- Set `ASPNETCORE_ENVIRONMENT=Development` if you want Swagger in the container:
```powershell
docker run -p 7012:7012 ^
  -e ASPNETCORE_ENVIRONMENT=Development ^
  ...same env vars... ^
  secure-login-access
```

## Common warnings (and fixes)
- **DataProtection key storage is ephemeral**: keys go to `/root/.aspnet/DataProtection-Keys` and will be lost when the container stops. For persistence:
  ```powershell
  docker run -p 7012:7012 ^
    -v dpkeys:/root/.aspnet/DataProtection-Keys ^
    ...env vars... ^
    secure-login-access
  ```
- **HTTPS redirection warning**: if you see `Failed to determine the https port for redirect`:
  - Option A: run in Development (`ASPNETCORE_ENVIRONMENT=Development`) or remove `UseHttpsRedirection` for container-only HTTP.
  - Option B: configure HTTPS binding and certs if you need TLS inside the container.

## Environment variables (overrides)
- `ConnectionStrings__DefaultConnection` – full SQL Server connection string.
- `Jwt__Issuer` – JWT issuer string.
- `Jwt__Audience` – JWT audience string.
- `Jwt__Secret` – HS256 signing secret (Base64 32+ bytes preferred).
- `Jwt__AccessTokenMinutes` – access token lifetime (minutes), optional.
- `ASPNETCORE_ENVIRONMENT` – `Development` to enable Swagger UI.

## Health check / quick test
After the container starts:
- `GET http://localhost:7012/weatherforecast` should return sample data.
- If you have valid users in the DB, `POST http://localhost:7012/api/auth/login` with body:
```json
{
  "emailOrUserName": "user@example.com",
  "password": "password"
}
```
Should return access/refresh tokens if credentials match the legacy membership tables.

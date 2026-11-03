# 🚀 Guía de Despliegue en Render — Firmeza ERP

Esta documentación detalla los pasos, configuraciones y soluciones técnicas aplicadas para desplegar la solución completa de **Firmeza ERP** en la nube de **Render**.

---

## 🏗️ 1. Arquitectura del Despliegue

El sistema se compone de 3 servicios independientes en Render:

```
+-----------------------------------------------------------+
|                     USUARIO / CLIENTE                     |
+-----------------------------------------------------------+
                             |
             (Navegador: HTTPS / Puerto 443)
                             v
+-----------------------------------------------------------+
| 🌐 Frontend: firmeza-client (Web Service / Docker)        |
| - Angular 22 + Nginx                                      |
| - URL: https://firmeza-client.onrender.com               |
+-----------------------------------------------------------+
                             |
              (Llamadas REST /api con JWT)
                             v
+-----------------------------------------------------------+
| ⚙️ Backend: firmeza-api (Web Service / Docker)            |
| - ASP.NET Core 10 Web API                                 |
| - URL: https://firmeza-api.onrender.com                   |
| - Swagger: https://firmeza-api.onrender.com/swagger       |
+-----------------------------------------------------------+
                             |
        (Conexión TCP con SSL / Puerto 5432)
                             v
+-----------------------------------------------------------+
| 🐘 Base de Datos: firmeza_db (PostgreSQL Administrado)   |
| - Servidor: dpg-dbkmg7rncjis73eogso0-a.ohio-postgres.render.com |
+-----------------------------------------------------------+
```

---

## 🐘 2. Base de Datos (PostgreSQL)

### A. Creación del Servicio
1. En Render: **New +** $\rightarrow$ **PostgreSQL**.
2. Nombre: `firmeza_db`.
3. Región: `Ohio (US East)` (o la de tu preferencia).
4. Plan: `Free`.

### B. Formato de Conexión para .NET (Npgsql)
Render entrega una URL tipo `postgresql://usuario:clave@host/db`. Sin embargo, el driver de .NET (**Npgsql / Entity Framework Core**) requiere el formato clave-valor ADO.NET con SSL habilitado:

```env
ConnectionStrings__DefaultConnection=Host=dpg-dbkmg7rncjis73eogso0-a.ohio-postgres.render.com;Port=5432;Database=firmeza_db;Username=firmeza_db_user;Password=ole4NWYF8dWTQtDd6YbR4FBNhxEiHJh5;Ssl Mode=Require;Trust Server Certificate=true
```

> [!TIP]
> Se utiliza el **host externo** (`.ohio-postgres.render.com`) para evitar problemas de resolución DNS interna cuando los servicios no comparten exactamente la misma red o región.

---

## ⚙️ 3. Backend (`firmeza-api`)

### A. Repositorio y Runtime
- **Repositorio:** `https://github.com/nuz777/FIrmezaERP.git`
- **Rama:** `main`
- **Runtime:** `Docker`
- **Dockerfile Path:** `Dockerfile.api`

### B. Variables de Entorno en Render
En la pestaña **Environment** de `firmeza-api`, configura:

| Variable | Valor | Descripción |
| :--- | :--- | :--- |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Entorno de producción .NET |
| `PORT` | `8080` | Puerto interno expuesto por Kestrel |
| `ConnectionStrings__DefaultConnection` | `Host=dpg-...ohio-postgres.render.com;...;Ssl Mode=Require;Trust Server Certificate=true` | Cadena de conexión a PostgreSQL |
| `Database__MigrateOnStartup` | `true` | Aplica migraciones y SeedAdmin automáticamente al arrancar |
| `Swagger__Enabled` | `true` | Permite visualizar Swagger en `/swagger` en producción |
| `Jwt__Issuer` | `Firmeza.Api` | Emisor del token JWT |
| `Jwt__Audience` | `Firmeza.Client` | Audiencia del token JWT |
| `Jwt__SigningKey` | `firmeza-super-secret-production-key-2026-secure-jwt-token-key!` | Clave secreta HMAC-SHA256 (mínimo 32 bytes) |
| `Jwt__AccessTokenMinutes` | `60` | Duración del token de acceso |
| `Jwt__RefreshTokenDays` | `7` | Duración del refresh token |
| `SeedAdmin__Email` | `admin@firmeza.local` | Correo del usuario administrador inicial |
| `SeedAdmin__Password` | `Admin123!` | Contraseña del administrador inicial |
| `Authentication__GitHub__ClientId` | `Ov23liUlAOW1r1YcfiiR` | Client ID de la OAuth App de GitHub |
| `Authentication__GitHub__ClientSecret` | `aef1e0908dbadccb394ddaa7cc0bbd0bd6895f08` | Client Secret de la OAuth App de GitHub |
| `Cors__AllowedOrigins__0` | `https://firmeza-client.onrender.com` | Origen frontend permitido por CORS |

### C. Ajuste de CORS en Código
En `API/src/Firmeza.Api/Program.cs` se configuró CORS para permitir de forma nativa cualquier subdominio de Render:
```csharp
if (uri.Host.Equals("firmeza.clandev.com", StringComparison.OrdinalIgnoreCase) ||
    uri.Host.EndsWith(".clandev.com", StringComparison.OrdinalIgnoreCase) ||
    uri.Host.EndsWith(".onrender.com", StringComparison.OrdinalIgnoreCase))
{
    return true;
}
```

---

## 🌐 4. Frontend (`firmeza-client`)

### A. Repositorio y Runtime
- **Repositorio:** `https://github.com/nuz777/FIrmezaERP-Client.git`
- **Rama:** `main`
- **Runtime:** `Docker`
- **Dockerfile Path:** `Dockerfile` (Node 22 para build + Nginx Alpine para servir la SPA)
- **Puerto:** `80`

### B. Resolución Dinámica de API
En `src/environments/environment.prod.ts` y `environment.ts`, el cliente detecta automáticamente cuando corre sobre Render y redirige sus peticiones a `firmeza-api`:
```typescript
if (hostname.endsWith('.onrender.com')) {
  const apiHost = hostname.includes('-client')
    ? hostname.replace('-client', '-api')
    : 'firmeza-api.onrender.com';
  return `https://${apiHost}/api`;
}
```

---

## 🔑 5. Autenticación Externa con GitHub (OAuth 2.0)

Para que el botón **"Iniciar sesión con GitHub"** funcione sin errores de redirección:

1. Ve a: **[GitHub Developer Settings > OAuth Apps](https://github.com/settings/developers)**.
2. Selecciona tu aplicación (`Ov23liUlAOW1r1YcfiiR`).
3. Configura los campos exactamente así:
   - **Application name:** `Firmeza ERP`
   - **Homepage URL:**
     ```text
     https://firmeza-client.onrender.com
     ```
   - **Authorization callback URL:**
     ```text
     https://firmeza-api.onrender.com/signin-github
     ```
     *(⚠️ Es obligatorio que termine en `/signin-github` para que el middleware de ASP.NET Core procese el código de autorización).*
4. Guarda los cambios con **Update application**.

### Retorno Seguro del Backend al Frontend:
En `API/src/Firmeza.Api/Controllers/AuthController.cs`, el método `NormalizeReturnUrl` valida que la redirección post-login sea segura y acepte dominios de Render:
```csharp
if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
    uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
    uri.Host.Equals("firmeza.clandev.com", StringComparison.OrdinalIgnoreCase) ||
    uri.Host.EndsWith(".clandev.com", StringComparison.OrdinalIgnoreCase) ||
    uri.Host.EndsWith(".onrender.com", StringComparison.OrdinalIgnoreCase))
{
    return returnUrl;
}
```

---

## 📋 6. Credenciales y Enlaces de Acceso

| Recurso | URL / Credencial |
| :--- | :--- |
| **Frontend Web** | [https://firmeza-client.onrender.com](https://firmeza-client.onrender.com) |
| **API Base** | [https://firmeza-api.onrender.com](https://firmeza-api.onrender.com) |
| **Swagger UI** | [https://firmeza-api.onrender.com/swagger](https://firmeza-api.onrender.com/swagger) |
| **Usuario Admin Inicial** | `admin@firmeza.local` |
| **Contraseña Admin Inicial** | `Admin123!` |
| **Repo Backend** | [https://github.com/nuz777/FIrmezaERP](https://github.com/nuz777/FIrmezaERP) |
| **Repo Frontend** | [https://github.com/nuz777/FIrmezaERP-Client](https://github.com/nuz777/FIrmezaERP-Client) |

# DataVortex — Serveur de licences

Serveur d'activation/vérification de licences (ASP.NET Core + PostgreSQL) et tableau de bord d'administration.
Il partage la signature de jeton avec le client via le projet `DataVortex.Licensing` : le bail signé par le
serveur est vérifié par le même code côté client.

## Prérequis

- .NET 8 SDK
- PostgreSQL 13+ (une base dédiée, p. ex. `datavortex_licenses`)

## Configuration (`appsettings.json` ou variables d'environnement)

| Clé | Rôle |
|---|---|
| `ConnectionStrings:Licenses` | Chaîne de connexion PostgreSQL. |
| `Admin:Email` / `Admin:Password` | Identifiants du **premier** SuperAdmin, créés au 1er démarrage. |
| `Security:AppHmacKey` | Clé HMAC partagée avec le client. **Vide = pas de signature de requête** (dev). Renseignez une clé aléatoire forte en prod. |

En production, passez les secrets par variables d'environnement (`ConnectionStrings__Licenses`, `Admin__Password`,
`Security__AppHmacKey`), jamais en clair dans le fichier.

## Premier démarrage

```bash
dotnet run --project src/DataVortex.LicenseServer
```

Au démarrage, le serveur :
1. **applique les migrations EF** (`Database.Migrate()`) — crée le schéma sur une base vide ;
2. **crée le SuperAdmin** depuis `Admin:Email`/`Admin:Password` et **journalise une fois** son **secret TOTP** —
   scannez-le dans une application d'authentification (Google Authenticator, etc.) ;
3. **génère la paire de clés de signature** (ECDSA P-256) si aucune n'existe.

### Récupérer la clé publique de signature

```bash
curl http://localhost:5000/api/v1/keys        # { "keys": ["<SPKI base64>", ...] }
```

## Câbler le client (DataVortex)

Dans `src/DataVortex.Core/Licensing/LicensingConstants.cs` :
- `PublicKeys` → la (les) clé(s) publique(s) renvoyée(s) par `/keys` ;
- `AppHmacKey` → **la même valeur** que `Security:AppHmacKey` du serveur ;
- `ServerCertSpkiPin` → (optionnel) le pin SPKI du certificat TLS du serveur.

Puis dans les réglages de l'app (`settings.json`) : `LicenseServerUrl` = URL du serveur, et **`LicensingEnabled = true`**
pour activer la barrière d'activation au démarrage.

## Générer une licence

- **Dashboard** : ouvrez `http://<serveur>/`, connectez-vous (e-mail + mot de passe + code TOTP), section *Générer*.
- **API** : `POST /api/v1/admin/licenses` avec un jeton de session (`Authorization: Bearer <token>` obtenu via
  `POST /api/v1/admin/login`). La clé n'est affichée **qu'une seule fois**.

## Rôles (RBAC)

| Rôle | Permissions |
|---|---|
| `Support` | Lecture (licences, stats, détail, anomalies, export) + révocation. |
| `Admin` | + génération, suspension, réactivation, réinitialisation des activations. |
| `SuperAdmin` | Tout. |

## Durcissement production

- **TLS** : placez le serveur derrière un reverse proxy (nginx/Caddy) en TLS 1.3 ; activez le cert pinning côté client.
- **Clé privée de signature** : le MVP la stocke en base (`signing_keys`). En production, déplacez-la vers un
  **KMS/HSM** (la signature passe alors par le KMS ; la clé n'est jamais exportable).
- **Rate limiting** : actif (120 req/min/IP) ; ajustez selon la charge.
- **HMAC de requête** : renseignez `Security:AppHmacKey` pour rejeter toute requête non signée ou rejouée.
- **Sauvegardes** : sauvegardes PostgreSQL chiffrées et testées, hors site.

## Migrations (évolution du schéma)

```bash
dotnet ef migrations add <Nom> --project src/DataVortex.LicenseServer
```
La migration s'applique automatiquement au prochain démarrage (`Database.Migrate()`).

## Endpoints

**Public** (client) : `POST /api/v1/{activate,verify,renew,deactivate}` · `GET /api/v1/{ping,keys}`
**Admin** : `POST /api/v1/admin/login` · `GET /api/v1/admin/{licenses,licenses/{id},stats,anomalies,export}` ·
`POST /api/v1/admin/licenses` · `POST /api/v1/admin/licenses/{id}/{revoke,suspend,reactivate,reset}`

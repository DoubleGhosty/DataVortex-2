# Déploiement — serveur de licence sur le VPS Windows + build client

Cible : VPS **Windows Server** `217.128.139.122`, **IP nue** (TLS auto-signé + épinglage côté client).

## Architecture (rappel)

```
 Clients (DataVortex.exe)  ──HTTPS 443──►  VPS 217.128.139.122
                                            ├─ API publique (ping/keys/activate/verify/renew/session)
                                            └─ Panel admin  (https://217.128.139.122/) : login + TOTP (2FA)
 Ton PC ──navigateur──► https://217.128.139.122/ → connexion → panel
```

- **Public** : le port 443 sert l'API client **ET** le panel admin (firewall : 443 seulement).
- **Admin** : panel web accessible depuis n'importe quel navigateur à `https://217.128.139.122/`, protégé par **login + TOTP (2FA)** + rate-limit strict sur le login (8 essais / 5 min / IP). Option : le restreindre à ton/tes IP via `Admin:AllowedIps`.
- **TLS** : cert **auto-signé** (SAN = `217.128.139.122` + `localhost`), le client l'accepte par **épinglage SPKI** (seul le détenteur de la clé privée peut compléter le handshake → pas de MITM).
- **Clés** : la paire de signature ECDSA P‑256 est **déjà générée**. La **publique** est câblée dans le client (`LicensingConstants`), la **privée** est dans `_secrets/` (va sur le VPS uniquement).

## Ce qui est déjà pré‑câblé (côté repo, committé)

`src/DataVortex.Core/Licensing/LicensingConstants.cs` :
- `DefaultServerUrl = https://217.128.139.122`
- `PublicKeys` = clé publique de signature de prod
- `ServerCertSpkiPin` = pin du cert TLS
- `AppHmacKey` = **injectée au build** (jamais dans le repo)

## Secrets (dossier `_secrets/`, gitignoré — à sauvegarder en lieu sûr)

| Fichier | Destination |
|---|---|
| `appsettings.Production.json` | VPS `C:\DataVortex\` (édite les 2 mots de passe) |
| `server-tls.pfx` | VPS `C:\DataVortex\` |
| `hmac-key.txt` | build client (auto) + secret GitHub `DV_HMAC_KEY` |
| `signing-private-pkcs8.b64.txt` | déjà injectée dans `appsettings.Production.json` |
| `signing-public-spki.b64.txt`, `tls-spki-pin.b64.txt` | déjà câblés dans le client |

> ⚠️ **`signing-private-pkcs8.b64.txt` = joyau.** Le perdre = devoir re‑générer une paire et re‑publier tous les clients. Sauvegarde `_secrets/` (hors repo).

---

## Étape 1 — PostgreSQL sur le VPS

Installe PostgreSQL (installeur Windows EDB), puis dans **psql** (ou pgAdmin) :

```sql
CREATE USER dvlicense WITH PASSWORD 'un-mot-de-passe-fort';
CREATE DATABASE datavortex_licenses OWNER dvlicense;
```

Le schéma est créé automatiquement (migrations EF) au 1er démarrage du serveur.

## Étape 2 — Publier le serveur (sur TON PC)

```bash
pwsh ./publish-server.ps1
```
→ `dist-server/` = `DataVortex.LicenseServer.exe` + `appsettings.json` + `wwwroot/`.

## Étape 3 — Copier sur le VPS dans `C:\DataVortex\`

- tout `dist-server/`
- `_secrets/appsettings.Production.json` → **édite** : remplace `REPLACE_WITH_DB_PASSWORD` (celui de l'étape 1) et `REPLACE_WITH_ADMIN_PASSWORD`
- `_secrets/server-tls.pfx`
- `deploy/install-service.ps1`

## Étape 4 — Premier lancement en console (pour récupérer le seed TOTP)

Le compte admin est créé au 1er démarrage et son **secret TOTP n'est affiché qu'une fois**. En service Windows la console est masquée → fais un 1er run manuel :

```powershell
cd C:\DataVortex
$env:ASPNETCORE_ENVIRONMENT = "Production"
.\DataVortex.LicenseServer.exe
```
Note la ligne `SuperAdmin créé ... Secret TOTP ... : XXXX`, enrôle ce secret dans une app d'authentification (Google/Microsoft Authenticator), puis **Ctrl+C**. (Si tu l'as raté : il est aussi dans l'Observateur d'événements → Journaux Windows → Application.)

## Étape 5 — Installer le service (PowerShell **Administrateur**)

```powershell
cd C:\DataVortex
.\install-service.ps1 -InstallDir C:\DataVortex
```
Crée le service auto‑start `DataVortexLicense`, le lance en Production, redémarre au crash, ouvre **443** en entrée (l'admin reste loopback).

## Étape 6 — Vérifier

Sur le VPS :
```powershell
curl.exe -k https://localhost/api/v1/ping
curl.exe -k https://localhost/api/v1/keys      # doit renvoyer la clé publique pré-câblée
curl.exe -k https://localhost/api/v1/admin/stats   # (sans token) 401 = OK, la route répond en loopback
```
Depuis ton PC (navigateur) :
- `https://217.128.139.122/api/v1/ping` → répond ✅
- `https://217.128.139.122/` → le **panel admin s'affiche** (accepte l'avertissement auto‑signé une fois) ✅

## Étape 7 — Panel admin depuis ton PC (navigateur, sans RDP)

Ouvre **`https://217.128.139.122/`** dans ton navigateur. Accepte l'avertissement de certificat auto‑signé **une fois** (ou importe le cert `server-tls.pfx` dans les *Autorités de certification racines de confiance* de ton PC pour ne plus l'avoir). Connecte‑toi (email + mot de passe + code TOTP) → tu gères/génères les licences depuis le panel.

Pas de RDP ni de tunnel. Le panel est protégé par **login + TOTP** + rate‑limit sur le login.

## Étape 8 — Build + signature + distribution du client

```bash
pwsh ./publish.ps1 -Output dist        # obfusqué + HMAC injecté (lit _secrets/hmac-key.txt automatiquement)
```
Puis **signe** `dist\DataVortex.exe` avec TON certificat de code‑signing :
```powershell
# cert dans le magasin Windows :
signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 dist\DataVortex.exe
# ou avec un .pfx :
signtool sign /fd SHA256 /f mon-cert.pfx /p MOT_DE_PASSE /tr http://timestamp.digicert.com /td SHA256 dist\DataVortex.exe
```
La signature active en plus l'auto‑contrôle anti‑tamper du client.

**Releases GitHub** : mets le secret repo `DV_HMAC_KEY` = contenu de `_secrets/hmac-key.txt` (Settings → Secrets and variables → Actions). Le workflow injectera l'HMAC dans l'exe publié. (La signature du client reste à faire par toi sur l'artefact, GitHub ne signe pas.)

---

## Sécurité & maintenance

- **Panel admin exposé sur Internet** : la sécurité repose sur le **login + TOTP (2FA)**. Mets un **mot de passe admin fort** (`Admin:Password` dans `appsettings.Production.json`) et garde le TOTP actif. Le login est limité à 8 essais / 5 min / IP.
- **Restreindre par IP (optionnel, recommandé si ton IP est fixe)** : dans `appsettings.Production.json`, ajoute
  ```json
  "Admin": { "Email": "...", "Password": "...", "AllowedIps": ["TON.IP.PUBLIQUE"] }
  ```
  Seules ces IP peuvent alors atteindre le panel (les autres → `404`). Vide/absent = ouvert (l'auth gère). Attention : si ton IP est dynamique, tu risques de te bloquer.
- **Firewall** : seul 443 est ouvert (API client + panel admin).
- **Rotation cert TLS** : régénère `server-tls.pfx`, mets à jour `ServerCertSpkiPin` (nouveau pin) dans le client et republie. Garde l'ancien pin en second temps si tu veux une transition douce (le client accepte plusieurs pins seulement si tu l'adaptes — sinon rotation = update client obligatoire).
- **Rotation clé de signature** : le serveur gère `kid`. Ajoute la nouvelle clé publique à `PublicKeys` (le client en accepte plusieurs) → publie l'update client → bascule l'active côté serveur → révoque l'ancienne après adoption.
- **Reverse proxy** : il n'y en a pas. Si tu en ajoutes un (IIS/nginx) devant, active `ForwardedHeaders` côté serveur sinon le filtre loopback verra l'IP du proxy et bloquera l'admin (ou pire, l'ouvrira).
- **Sauvegardes** : la base `datavortex_licenses` (licences + clé de signature) et `_secrets/`.

## Dépannage

- **Le service ne démarre pas** : la base doit être joignable (migration au démarrage). Vérifie le mot de passe DB dans `appsettings.Production.json` et que PostgreSQL tourne. Logs → Observateur d'événements → Application.
- **Le client n'active pas** : vérifie `https://217.128.139.122/api/v1/ping` depuis le réseau du client, et que le pin/clé publique du client correspondent au `server-tls.pfx`/à la clé privée déployés (mêmes `_secrets/`).
- **`401 requête non authentifiée`** sur activate/verify : l'HMAC client ≠ serveur. Le build client doit avoir la même `DV_HMAC_KEY` que `Security:AppHmacKey` du serveur (tous deux = `_secrets/hmac-key.txt`).

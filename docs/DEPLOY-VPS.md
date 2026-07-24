# Déploiement — serveur de licence (VPS Windows) + build client

Cible : VPS **Windows Server** `217.128.139.122`, **IP nue** (TLS auto-signé + épinglage côté client).
Le serveur est **turnkey** : un exe qu'on lance. **Aucune base de données à installer** (SQLite embarqué).

## Architecture

```
 Clients (DataVortex.exe)  ──HTTPS 443──►  VPS 217.128.139.122
                                            ├─ API publique (ping/keys/activate/verify/renew/session)
                                            └─ Panel admin  (https://217.128.139.122/) : login + TOTP (2FA)
 Ton PC ──navigateur──► https://217.128.139.122/ → connexion → panel
```

- **Public** : le port 443 sert l'API client **et** le panel admin (firewall : 443 seulement).
- **Admin** : panel web, depuis n'importe quel navigateur, protégé par **login + TOTP** + rate-limit (8/5 min/IP). Option `Admin:AllowedIps` pour restreindre à ton IP.
- **TLS** : cert **auto-signé** (SAN `217.128.139.122` + `localhost`), accepté par le client via **épinglage SPKI**.
- **Base** : **SQLite** — le fichier `datavortex_licenses.db` est créé à côté de l'exe au 1er lancement. Rien à provisionner.
- **Clés** : paire de signature ECDSA P-256 déjà générée ; la publique est câblée dans le client, la privée est dans le bundle serveur (jamais dans le repo).

## Secrets (`_secrets/`, gitignoré — à sauvegarder en lieu sûr)

| Fichier | Rôle |
|---|---|
| `appsettings.Production.json` | config prod remplie (clé privée de signature, HMAC, mot de passe du cert) — **copiée automatiquement dans le bundle** par `publish-server.ps1` |
| `server-tls.pfx` | cert TLS — **copié automatiquement dans le bundle** |
| `hmac-key.txt` | clé HMAC (build client auto + secret GitHub `DV_HMAC_KEY`) |
| `signing-private-pkcs8.b64.txt` | déjà injectée dans `appsettings.Production.json` |

> ⚠️ **`signing-private-pkcs8.b64.txt` = joyau.** Le perdre = re-générer une paire et re-publier tous les clients. Sauvegarde `_secrets/` **hors repo**.

---

## Déployer le serveur — 3 étapes

**1. Construire le bundle (sur ton PC)**
```bash
pwsh ./publish-server.ps1
```
→ `dist-server/` = exe + `wwwroot/` + `appsettings.json` + `appsettings.Production.json` + `server-tls.pfx`. Prêt à copier.

**2. Copier `dist-server/` sur le VPS** (ex. `C:\DataVortex\`), puis **lancer l'exe une fois en console** pour récupérer les identifiants admin :
```powershell
cd C:\DataVortex
.\DataVortex.LicenseServer.exe
```
Le 1er lancement crée la base SQLite et affiche **une seule fois** :
```
SuperAdmin créé (admin@datavortex.app). Mot de passe généré (affiché une seule fois) : XXXXXXXX
Secret TOTP admin à enrôler ... : YYYYYYYY
```
Note le **mot de passe** + enrôle le **secret TOTP** dans Google/Microsoft Authenticator. Puis **Ctrl+C**.
*(Tu peux fixer ton propre mot de passe à la place : mets-le dans `appsettings.Production.json` → `Admin:Password` avant de lancer.)*

**3. Installer le service** (PowerShell **Administrateur**)
```powershell
cd C:\DataVortex
.\install-service.ps1 -InstallDir C:\DataVortex
```
Service auto-start `DataVortexLicense`, redémarre au crash, ouvre **443** en entrée.

## Vérifier

Sur le VPS : `curl.exe -k https://localhost/api/v1/ping` → `{"status":"ok"}`.
Depuis ton PC : `https://217.128.139.122/api/v1/ping` répond ; `https://217.128.139.122/` affiche le panel.

## Panel admin (depuis ton PC, navigateur)

Ouvre **`https://217.128.139.122/`**, accepte l'avertissement auto-signé **une fois** (ou importe `server-tls.pfx` dans les *Autorités racines de confiance* de ton PC), connecte-toi (email `admin@datavortex.app` + mot de passe + code TOTP), génère/gère les licences. Pas de RDP.

## Build + signature + distribution du client

```bash
pwsh ./publish.ps1 -Output dist        # obfusqué + HMAC injecté (lit _secrets/hmac-key.txt automatiquement)
```
Puis **signe** `dist\DataVortex.exe` avec **ton** certificat de code-signing :
```powershell
signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 dist\DataVortex.exe
```
Distribue `dist\DataVortex.exe`.

**Releases GitHub (client uniquement)** : mets le secret repo `DV_HMAC_KEY` = contenu de `_secrets/hmac-key.txt`, puis pousse un tag `vX.Y.Z`. Le serveur, lui, **n'est jamais** publié (il contient ta clé privée) : tu le construis en local.

> ⚠️ **Cohérence HMAC** : le client et le serveur doivent avoir la **même** clé HMAC. Le build local (`publish.ps1`) et le serveur la prennent tous deux de `_secrets/` → cohérents. Pour la release GitHub, le client n'a l'HMAC que si `DV_HMAC_KEY` est posé ; sinon serveur et client doivent être tous deux sans HMAC.

---

## Sécurité & maintenance

- **Panel admin exposé** : sécurité = **login + TOTP**. Mot de passe admin fort ; login limité à 8/5 min/IP.
- **Restreindre par IP (option)** : `"Admin": { …, "AllowedIps": ["TON.IP"] }` dans `appsettings.Production.json`.
- **Firewall** : 443 seulement.
- **Sauvegardes** : `_secrets/` + le fichier `C:\DataVortex\datavortex_licenses.db` (licences + clé de signature).
- **Rotation clé de signature** : le serveur gère `kid` ; ajoute la nouvelle clé publique à `LicensingConstants.PublicKeys` → publie l'update client → bascule côté serveur.
- **Reverse proxy** : s'il y en a un devant, active `ForwardedHeaders` sinon le filtre `AllowedIps` verra l'IP du proxy.

## Dépannage

- **Service ne démarre pas** : logs → Observateur d'événements → Application. Le cert `server-tls.pfx` doit être dans le dossier de l'exe.
- **Client n'active pas** : vérifie `https://217.128.139.122/api/v1/ping` depuis le réseau du client ; le pin/clé publique du client doivent correspondre au `server-tls.pfx`/à la clé privée déployés (mêmes `_secrets/`).
- **`401 requête non authentifiée`** : HMAC client ≠ serveur (voir la note Cohérence HMAC).

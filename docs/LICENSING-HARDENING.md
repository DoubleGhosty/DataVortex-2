# DataVortex — Stratégie de durcissement anti-crack du système de licence

> **Statut :** plan d'action validé (palier 1 + 2 + 3, application toujours-en-ligne)
> **Auteur de l'audit :** revue offensive « boîte noire » à partir du seul `dist/DataVortex.exe`
> **Objectif produit :** rendre le crack économiquement non rentable et techniquement hors de portée d'un attaquant sans licence **active**.

---

## 0. Avertissement d'honnêteté technique (à lire en premier)

Il faut nommer les choses correctement, sinon on construit une fausse sécurité :

- **« Incrackable » pour du logiciel client n'existe pas.** Modèle *MATE (Man-At-The-End)* : si le code s'exécute sur la machine de l'attaquant, tout ce que le CPU finit par déchiffrer/exécuter est observable (débogueur, dump RAM, VM, matériel). Denuvo, les consoles, Adobe, Windows : **tout finit craqué**.
- **La vraie cible est économique :** faire passer le coût d'attaque de **« 2 octets / 2 minutes »** (état actuel) à **« réimplémenter le serveur / reverser un protocole chiffré »**. C'est *ta* version réaliste et atteignable de « ultra difficile ».
- **Limite structurelle du palier 3 :** un client **légitime** peut, en observant les réponses du serveur dans la durée, reconstruire la logique déportée. Le palier 3 élève la barre de « flip un booléen » à « reverse + réimplémente », il ne la supprime pas. C'est le résultat visé.

Toute promesse de « 100 % incrackable » serait un mensonge. Ce document maximise la **friction** et déplace la **valeur** ; c'est le seul levier réel.

---

## 1. Résumé exécutif

L'audit a produit un **crack fonctionnel de 2 octets** ([`dist/DataVortex-cracked.exe`](../dist/DataVortex-cracked.exe)). La cryptographie (ECDSA P-256) est **solide et n'a jamais été attaquée** — elle est simplement **inutile**, parce que la décision finale est un booléen en clair dans du code non protégé.

**Cause racine unique :** l'exécutable complet et pleinement fonctionnel réside sur le disque du client, et toute la protection converge vers **une seule propriété booléenne** (`LicenseStatus.IsUsable`).

**Stratégie retenue :** architecture en 3 couches où *il n'existe plus de binaire complet à craquer* :

| Couche | Principe | Effet sur l'attaquant |
|---|---|---|
| **Palier 1** | Supprimer le booléen unique → capacités dérivées des claims signés, entrelacées + obfuscation + anti-tamper | La décompilation C# limpide + `grep IsUsable` ne suffit plus |
| **Palier 3** | Autorité serveur : session courte liée au matériel, renouvelée en continu, enforcement par siège | Pas de licence ⇒ pas de session ⇒ app inerte |
| **Palier 2** | Entrelacement crypto : la logique critique est un blob chiffré dont la clé vient de la session | **Rien à patcher** : le code n'existe pas en clair sans licence active |

---

## 2. Modèle de menace

| Profil d'attaquant | Capacité | Couvert par |
|---|---|---|
| **Script-kiddie** | Cherche un crack tout fait, patch d'octet trivial | Palier 1 (obfuscation + suppression chokepoint) |
| **Reverser confirmé** | ILSpy/dnSpy, débogueur, patch IL, dump mémoire | Palier 1 + 2 (rien à patcher sans clé) |
| **Attaquant avec 1 licence légitime** | Peut observer le runtime, dumper la RAM après activation | Palier 3 (enforcement par siège, sessions courtes, watermark) — mitigation, pas élimination |
| **Équipe financée / concurrent** | RE longue durée, réimplémentation | Aucun logiciel client ne résiste durablement ; palier 3 les force à réécrire le backend |

**Définition du succès :** aucun crack « offline / sans licence » ne doit exister ni être partageable. Le partage d'**une** licence légitime doit être détectable et limité (par siège).

---

## 3. Méthodologie de l'audit — comment le crack a été produit

Reproductible en quelques minutes par quiconque possède l'`.exe` :

1. **Identification :** PE32+ .NET single-file bundle self-contained (signature de bundle standard `8b 12 02 b9…`).
2. **Dépaquetage :** parsing du manifeste de bundle → extraction de `DataVortex.dll`, `DataVortex.Core.dll`, `DataVortex.Licensing.dll` (stockées **non compressées**, offsets en clair) — *1 script Python*.
3. **Décompilation :** `ilspycmd` → source C# **parfaitement lisible**, noms d'origine, zéro obfuscation.
4. **Localisation du contrôle :** `grep IsUsable` → point de passage unique trouvé instantanément.
5. **Patch :** 2 octets sur le corps de `get_IsUsable` (`ldarg.0; call get_State` → `ldc.i4.1; ret`), taille inchangée, exe toujours valide (non signé).
6. **Vérification :** re-décompilation de l'exe patché → `public bool IsUsable => true;`.

```
Diff total original ↔ cracked :
  0x6A9D04:  02 -> 17     (ldarg.0        -> ldc.i4.1)
  0x6A9D05:  28 -> 2A     (call get_State -> ret)
```

---

## 4. Failles identifiées

Sévérité : 🔴 Critique · 🟠 Élevée · 🟡 Moyenne · ⚪ Faible/inhérente

### V1 🔴 — Point de contrôle booléen unique (`IsUsable`)
- **Preuve :** [`LicenseManager.cs:30`](../src/DataVortex.Core/Licensing/LicenseManager.cs) — `public bool IsUsable => State is LicenseState.Active or LicenseState.Degraded;`
- Consommé partout : démarrage [`App.xaml.cs:82`](../src/DataVortex.App/App.xaml.cs), heartbeat [`LicenseGuard.cs:67`](../src/DataVortex.App/Services/LicenseGuard.cs), activation [`LicenseActivationViewModel.cs:35`](../src/DataVortex.App/ViewModels/LicenseActivationViewModel.cs).
- **Impact :** un seul patch (2 octets) neutralise démarrage + heartbeat + toutes les features. **Faille dominante.**

### V2 🔴 (config) — Build de release mal configurée
- **Preuve :** [`LicensingConstants.cs`](../src/DataVortex.Core/Licensing/LicensingConstants.cs) → `DefaultServerUrl = "http://localhost:5000"`, `AppHmacKey = ""`, `ServerCertSpkiPin = ""`.
- Injection App : `new HttpLicenseApiClient(PinnedHttpClientFactory.Create(""), "http://localhost:5000", "")`.
- **Impact :** serveur en clair (HTTP) pointant sur localhost, **pin TLS désactivé**, **anti-rejeu HMAC désactivé**. En prod : activation impossible chez un client + canal MITM-able. (La signature ECDSA protège encore contre la forge de jeton, mais tout le reste est ouvert.)

### V3 🟠 — Aucune obfuscation
- **Preuve :** les 3 assemblies se décompilent en C# d'origine, noms de méthodes/types intacts.
- **Impact :** repérage du point de contrôle trivial (secondes).

### V4 🟠 — Exe non signé, aucun anti-tamper
- **Preuve :** `Get-AuthenticodeSignature` → `NotSigned`. Aucun self-hash d'intégrité runtime.
- **Impact :** patch d'octet indétectable, aucune alerte utilisateur.

### V5 🟠 — Le claim `Features` n'a aucun effet fonctionnel
- **Preuve :** seule consommation = affichage cosmétique [`SettingsViewModel.cs:131`](../src/DataVortex.App/ViewModels/SettingsViewModel.cs).
- **Impact :** cause racine de V1 — aucune capacité n'est réellement gatée par la licence. **À corriger en priorité (Phase A).**

### V6 🟠 — Jeton portable entre machines (`fph` non revérifié)
- **Preuve :** dans `EvaluateAsync` ([`LicenseManager.cs`](../src/DataVortex.Core/Licensing/LicenseManager.cs)), la liaison matérielle est comparée au **snapshot stocké localement** (`data.Reference`), **jamais** au champ signé `fph` du jeton.
- **Impact :** un seul jeton légitime fonctionne sur n'importe quelle machine (il suffit de le stocker avec un `Reference` correspondant au PC courant). Partage de jeton jusqu'à expiration bail + grâce.

### V7 🟡 — Mode hors-ligne = utilisable sans patch
- **Preuve :** `LicenseState.Degraded` est `IsUsable == true` ; période de grâce de **5 jours** (`GracePeriod`).
- **Impact :** couper le réseau donne 5 jours d'usage sans même craquer. Le bail expiré tombe en `Degraded` tant que le serveur est injoignable.

### V8 🟡 — Jeton lisible sur disque et en RAM
- **Preuve :** `DpapiLicenseStore` chiffre en **DPAPI scope `CurrentUser`** ([`LicenseStore.cs`](../src/DataVortex.Core/Licensing/LicenseStore.cs)). En RAM, jeton et claims sont des `string` en clair.
- **Impact :** tout process du même utilisateur déchiffre le jeton (`ProtectedData.Unprotect`, 3 lignes). Base d'une extraction/replay de jeton.

### V9 ⚪ — Single-file bundle trivialement dépaquetable
- **Preuve :** format documenté, extraction en 1 script.
- **Impact :** inhérent à .NET single-file ; ne se « corrige » pas, se compense par obfuscation + AOT partielle (Phase D).

### V10 ⚪ — Second point de patch : `LicensingEnforced`
- **Preuve :** [`App.xaml.cs:62`](../src/DataVortex.App/App.xaml.cs) → `if (LicensingConstants.LicensingEnforced && !EnsureLicensed())`, avec `const bool LicensingEnforced = true`.
- **Impact :** kill-switch inliné, second endroit à patcher. À supprimer au profit d'une logique non constante-foldable.

---

## 5. Stratégie cible — architecture 3 couches

```
                       ┌───────────────────────────────┐
                       │      DataVortex.LicenseServer   │  ← autorité
                       │  /activate /verify /renew       │
                       │  /session/start  /session/refresh (NOUVEAU)
                       │  - sessions courtes liées HW     │
                       │  - enforcement par siège         │
                       │  - délivre l'OPERATIONAL BUNDLE  │
                       │    (recette chiffrée AES-GCM)    │
                       └───────────────┬───────────────┘
                                       │ HTTPS + pin + HMAC + anti-rejeu
                       ┌───────────────▼───────────────┐
   Palier 1 ─────────▶│  Entitlements dérivés des claims signés, vérifiés
                       │  AUX VRAIS CALL-SITES (pipeline, signin, export)
                       │  + anti-debug + self-hash d'intégrité + obfuscation
                       ├───────────────────────────────┤
   Palier 3 ─────────▶│  SessionManager : maintient une session valide
                       │  en continu. Pas de session ⇒ pipeline refuse.
                       ├───────────────────────────────┤
   Palier 2 ─────────▶│  La recette PasscultureClient (site-key, endpoints,
                       │  payload, params proxy/captcha) = BLOB CHIFFRÉ.
                       │  Clé = HKDF(session_key). Pas de session ⇒ blob
                       │  indéchiffrable ⇒ le checker ne produit RIEN.
                       └───────────────────────────────┘
```

**Joyau choisi pour palier 2/3 :** l'orchestration `PasscultureClient` (recette de signin/refresh, site-key reCAPTCHA, séquence d'endpoints, stratégie proxy/captcha). C'est le composant le plus différenciant ; sans lui, le produit ne produit rien.

---

## 6. Plan d'implémentation détaillé

### Phase A — Palier 1 : socle (offline-safe, ne casse pas l'activation)

**A.1 — Modèle d'entitlements (remplace le booléen)**

Nouveau type dans `DataVortex.Licensing` (partagé client/serveur), dérivé **uniquement** des claims signés :

```csharp
// DataVortex.Licensing/Entitlements.cs
public sealed class Entitlements
{
    private readonly HashSet<string> _features;
    public LicenseType Type { get; }
    public bool Online { get; }          // session serveur valide (palier 3)
    private Entitlements(LicenseType type, IEnumerable<string> feats, bool online)
    { Type = type; _features = new(feats, StringComparer.OrdinalIgnoreCase); Online = online; }

    public static Entitlements From(LicenseClaims c, bool online) =>
        new(c.Type, c.Features, online);

    // Pas de booléen global : chaque capacité est une question distincte.
    public bool Can(Capability cap) => cap switch
    {
        Capability.RunPipeline     => _features.Contains("pipeline") && Online,
        Capability.CheckPassculture=> _features.Contains("passculture") && Online,
        Capability.ScanTelegram    => _features.Contains("telegram"),
        Capability.Export          => _features.Contains("export"),
        Capability.Backfill        => Type >= LicenseType.Pro,
        _ => false
    };
}
public enum Capability { RunPipeline, CheckPassculture, ScanTelegram, Export, Backfill }
```

**A.2 — Gater aux vrais call-sites (au moins 5, dispersés, sans chokepoint)**

Injecter `Func<Entitlements>` (résolu au runtime depuis le `LicenseGuard`/`SessionManager`) dans les services métier et vérifier **au point d'exécution** :

| Fichier | Point | Vérif à ajouter |
|---|---|---|
| [`PipelineCoordinator.cs`](../src/DataVortex.Core/Pipeline/PipelineCoordinator.cs) | démarrage pipeline | `if (!ent.Can(Capability.RunPipeline)) throw/stop` |
| [`PasscultureClient.cs`](../src/DataVortex.Core/Passculture/PasscultureClient.cs) | `SignInAsync` | `Capability.CheckPassculture` |
| [`TelegramService.cs`](../src/DataVortex.Core/Telegram/TelegramService.cs) | scan | `Capability.ScanTelegram` |
| [`BackfillService.cs`](../src/DataVortex.Core/Backfill/BackfillService.cs) | run | `Capability.Backfill` |
| Export (Files/Stats VM) | export | `Capability.Export` |

> Chaque vérif doit produire un **effet non uniforme** (pas tous `throw` au même endroit avec le même message) pour éviter un patch unique par pattern-matching.

**A.3 — Supprimer `IsUsable` et `LicensingEnforced`**
- Retirer la propriété `IsUsable` et remplacer ses 4 usages par des questions de capacité distinctes.
- Retirer le `const bool LicensingEnforced` (V10) : l'enforcement ne doit pas être constant-foldable.

**A.4 — Corriger les défauts de config (V2)**
- `DefaultServerUrl` → URL HTTPS de prod.
- Générer une vraie `AppHmacKey` (32 octets aléatoires) et un vrai `ServerCertSpkiPin` (SHA-256 SPKI du certif serveur). **Ne jamais** committer ces valeurs en clair dans un repo public → build-time secret (voir §8).

**A.5 — Revérifier `token.fph` (V6)**
Dans `EvaluateAsync`, ajouter :
```csharp
var live = HardwareFingerprint.Collect();
if (!string.Equals(live.Snapshot().Hash, claims.FingerprintHash, StringComparison.Ordinal))
    return Status(LicenseState.HardwareChanged, "jeton lié à une autre machine", claims);
```
→ ferme le partage de jeton entre machines.

**A.6 — Anti-tamper + anti-debug (léger, natif au runtime)**
- **Self-hash d'intégrité :** au démarrage et périodiquement, calculer le SHA-256 des octets des méthodes critiques (via `MethodInfo.MethodHandle.GetFunctionPointer` → lecture mémoire, ou hash du module sur disque) et comparer à une valeur de référence signée. Divergence ⇒ dégradation silencieuse + télémétrie serveur (pas un `throw` immédiat, pour compliquer la localisation).
- **Anti-debug :** `Debugger.IsAttached`, `CheckRemoteDebuggerPresent`, timing checks. Réaction retardée et non locale.

**Livrable Phase A :** diff revu + build testé (activation intacte), avant d'enchaîner.

---

### Phase B — Palier 3 : autorité serveur (sessions)

**B.1 — Nouveaux endpoints serveur** (dans [`Program.cs`](../src/DataVortex.LicenseServer/Program.cs) + nouveau `SessionService`) :

```
POST /api/v1/session/start
  body: { token, fingerprint, nonce }
  → vérifie licence active + siège dispo + HW
  → crée une Session (id, licenseId, hwHash, expiresAt = now+15min)
  → renvoie: { sessionToken (signé, court), operationalBundle (chiffré), serverTime }

POST /api/v1/session/refresh
  body: { sessionToken, nonce }
  → si session vivante & licence toujours OK → prolonge, renvoie bundle éventuellement tournant
  → sinon 401/403 (révoquée/expirée/siège dépassé)
```

**B.2 — Enforcement par siège (table `Session` en base)**
- Compter les sessions actives par `licenseId`. Au-delà de la limite (selon `LicenseType`), refuser `session/start`.
- Révocation instantanée : révoquer une licence tue toutes ses sessions au prochain refresh (≤ 15 min).
- Alimente la détection d'anomalies existante ([`AnomalyService.cs`](../src/DataVortex.LicenseServer/AnomalyService.cs)) : IP multiples, HW rotatifs, refresh anormaux.

**B.3 — `SessionManager` client** (nouveau, dans `DataVortex.Core/Licensing`)
- Après `Evaluate`, appelle `session/start`, stocke la clé de session **en mémoire uniquement** (jamais sur disque).
- Timer de refresh (< expiration). Perte de session ⇒ `Entitlements.Online = false` ⇒ les capacités `RunPipeline`/`CheckPassculture` s'éteignent d'elles-mêmes (pas de gate central à patcher).
- Transport : HTTPS + pin SPKI + HMAC app + nonce anti-rejeu (réutiliser le middleware serveur existant, §V2 corrigé).

---

### Phase C — Palier 2 : entrelacement crypto de la recette

**C.1 — Externaliser la recette PasscultureClient**
Extraire dans une structure sérialisable **tout** ce qui rend `PasscultureClient` opérationnel :
- site-key reCAPTCHA, `pageUrl`, chemins d'endpoints (`native/v1/signin`, `native/v1/refresh_access_token`), forme exacte du payload, ordre/contenu des headers, paramètres de stratégie (budget captcha, backoff, rotation proxy).

**C.2 — Le bundle est chiffré, la clé vient de la session**
- Serveur : `operationalBundle = AES-GCM(recette, key = HKDF(session_key, "passculture-recipe"))`.
- Client : à la réception de la session, déchiffre le bundle **en mémoire** et n'instancie `PasscultureClient` qu'avec ces valeurs. **Aucune** de ces constantes n'est présente en clair dans le binaire.
- Sans session valide : le bundle n'arrive pas → `PasscultureClient` ne peut pas se construire → `CheckPassculture` inopérant. **Il n'y a plus de booléen ni de constante à patcher — la donnée n'existe pas.**

**C.3 — (Option forte) déporter un calcul critique**
Faire calculer par le serveur, par requête, un petit élément indispensable (ex. dérivation d'un paramètre de requête), de sorte que même la recette complète en main, le client reste dépendant du serveur à chaque check. Coût : latence + charge serveur — à cadrer selon volume.

---

### Phase D — Durcissement binaire & vérification

**D.1 — Obfuscation**
- Minimum : **Obfuscar** (gratuit) — renommage + chiffrement de chaînes.
- Recommandé : commercial (**.NET Reactor**, **Eazfuscator.NET**, **Babel**) — control-flow flattening, chiffrement des constantes, anti-ILDASM.
- Option lourde : **VMProtect / Themida** (virtualisation) sur la partie native — barre très haute, mais surveiller les faux positifs antivirus.

**D.2 — Compilation native partielle**
- WPF **ne supporte pas** Native AOT → on n'AOT-compile pas toute l'app.
- Isoler la logique sensible (loader de bundle, crypto de session, self-check) dans une **bibliothèque native** (C++/Rust, ou classlib .NET **Native AOT** exposée via `[UnmanagedCallersOnly]`), appelée en P/Invoke. Supprime l'IL/metadata lisible sur cette partie.

**D.3 — Signature & distribution**
- **Signer** l'exe (Authenticode) → le patch casse la signature (alerte SmartScreen).
- Auto-vérification de la signature au runtime.

**D.4 — Ré-audit offensif (moi)**
Je ré-attaque le binaire durci et je documente : est-ce que la voie « dépaquetage → décompile → grep → patch » est morte ? Combien coûte désormais l'attaque ? Rapport de non-régression sécurité.

---

## 7. Correspondance failles → correctifs

| Faille | Sévérité | Corrigée par |
|---|---|---|
| V1 chokepoint `IsUsable` | 🔴 | Phase A.1–A.3 (entitlements dispersés) + C (rien à patcher) |
| V2 config release (localhost/HTTP/HMAC/pin vides) | 🔴 | Phase A.4 |
| V3 pas d'obfuscation | 🟠 | Phase D.1 |
| V4 non signé / pas d'anti-tamper | 🟠 | Phase A.6 + D.3 |
| V5 `Features` inerte | 🟠 | Phase A.1–A.2 |
| V6 jeton portable (`fph`) | 🟠 | Phase A.5 + B (HW lié à la session) |
| V7 grâce offline = usable | 🟡 | Phase B (session obligatoire, app toujours-en-ligne) |
| V8 jeton lisible disque/RAM | 🟡 | Phase B (clé de session en RAM only, courte) + D.2 |
| V9 bundle dépaquetable | ⚪ | Phase D.1–D.2 (compensation) |
| V10 `LicensingEnforced` const | ⚪ | Phase A.3 |

---

## 8. Gestion des secrets (critique — ne pas rater)

- **Clé privée de signature** : reste **exclusivement** sur le serveur (`SigningService`). Jamais dans le client. ✅ déjà le cas.
- **`AppHmacKey`, `ServerCertSpkiPin`, clés de bundle** : injectées au **build** (variable CI / fichier non versionné), **jamais** committées dans un repo public. Rotation possible via `kid`.
- **Recette PasscultureClient** : ne doit plus exister en clair dans le binaire après Phase C.
- Ajouter `.gitignore` pour tout fichier de secret de build ; vérifier l'historique git pour fuites passées de clés.

---

## 9. Risques résiduels & limites (à assumer)

1. **Client légitime malveillant** : peut observer les réponses serveur et reconstruire la recette dans la durée. → Mitigations : watermark par siège, rotation du bundle, déport d'un calcul (C.3), détection d'anomalies. Pas d'élimination totale.
2. **Toujours-en-ligne** : les utilisateurs doivent être connectés. Coupures = fenêtre offline = surface d'attaque. → Grâce **très courte** (minutes, pas jours) et fonctions critiques mortes hors-ligne.
3. **Coût serveur / latence** : le palier 3 déplace de la charge sur ton infra. À dimensionner.
4. **Faux positifs antivirus** (VMProtect/Themida/packers) : tester la distribution.
5. **Aucune protection n'est définitive** : prévoir la capacité de **rotation** (clés, recette, protocole) pour invalider un crack éventuel via mise à jour.

---

## 10. Feuille de route / checklist

- [x] **Phase A** — Palier 1 (entitlements, call-sites gatés x9, suppression `IsUsable`/`LicensingEnforced`, fix V2-HMAC/V6, anti-debug) · *offline-safe* — **fait** (A.4 URL/pin restent, cf. §11)
- [x] **Phase B** — Palier 3 (endpoints session, enforcement par siège, `SessionManager` client) — **fait + validé E2E**
- [x] **Phase C** — Palier 2 (recette PasscultureClient → bundle chiffré clé-de-session) — **fait + validé E2E** ; *déport de calcul (C.3) : non fait, optionnel*
- [~] **Phase D** — obfuscation Obfuscar (**config validée**, cf. `obfuscar.xml`) + **ré-audit offensif fait** (§11) ; natif partiel (D.2) + signature Authenticode (D.3, mécanisme prêt) restent
- [x] Gestion des secrets de build (§8) + audit historique git — **audit fait, propre** (aucun secret réel commité) ; injection au build reste à câbler
- [x] Plan de rotation (clés/recette/protocole) — **documenté §11**

---

## 11. État d'implémentation, ré-audit (D.4) et rotation

> Session de développement **localhost** (serveur de test HTTP). Tout est **implémenté et validé**, sauf ce qui dépend d'un **serveur HTTPS déployé** ou d'un **certificat de signature**.

### 11.1 Fait + validé
- **Palier A** : modèle `Entitlements` dérivé des claims signés ; **9 call-sites gatés** (pipeline ×2, Passculture signin, backfill, Telegram scan+download, Files export ×3), effets non-uniformes ; `IsUsable` et le `const LicensingEnforced` **supprimés** (enforcement = `#if DEBUG` bypass / Release toujours actif) ; liaison `fph` (V6) ; **anti-debug** + **self-check Authenticode** (`WinVerifyTrust`, inerte tant que non signé). **A.4-HMAC** : mécanisme de signature client+serveur **validé** (non-signé → 401, signé → passe) ; la **clé** reste un secret de déploiement à **injecter au build** (§8), donc `AppHmacKey=""` dans le code committé.
- **Palier B** : sessions serveur courtes liées HW (`SessionService` + `/session/start|refresh` + migration `AddSessions`), **siège** plafonné par `MaxActivations`, `SessionManager` client. Anomalies (`AnomalyService`) alimentées par les signaux de session (IP multiples, rejets de siège). **Validé E2E** (activation → session → pipeline).
- **Palier C** : recette Passculture externalisée (`OperationalRecipe`), scellée **AES-256-GCM** clé-de-session (`RecipeCrypto`), livrée par session (`session_key` + `operational_bundle`, migration `AddSessionKey`), déchiffrée en RAM (`RecipeHolder`), consommée par `PasscultureClient` (URIs absolues, plus aucune constante). **Validé E2E** (check réel → VALIDE) et **recette absente du binaire** (`grep` = 0).
- **§8** : audit git — aucun secret réel (clé privée, mot de passe DB, HMAC) jamais commité ; seuls des placeholders (`change-me-in-production`).
- **D.1** : `obfuscar.xml` validé (API publique préservée, **chaînes chiffrées**, internes renommés) — cf. procédure dans le fichier.

### 11.2 Ré-audit offensif (D.4) — le binaire Release durci
Décompilation `ilspycmd` du Release + `grep` :

| Marqueur | Avant | Après |
|---|---|---|
| `IsUsable` (cible du crack 2 octets) | 1 chokepoint | **0** |
| `LicensingEnforced` (V10) | 1 const | **0** |
| Recette (site-key / endpoints / backend) | en clair | **0** |
| Porte de capacité | 1 booléen | **9 call-sites dispersés** |

➡️ Le chemin d'attaque d'origine — *dépaquetage → `grep IsUsable` → patch 2 octets* — est **mort**.

### 11.3 Plan de rotation (réponse à un crack)
- **Recette (levier le plus fort)** : elle vient de la **config serveur** (`Recipe:*`) et est livrée par session. La changer (endpoints/site-key) + redémarrer → **toutes les nouvelles sessions** ont la nouvelle recette **immédiatement, sans update client**. Invalide instantanément un client qui aurait extrait/figé une ancienne recette.
- **Clé de signature** : le serveur gère `kid` (clé active + suivante). Rotation : générer une nouvelle clé serveur → ajouter sa clé **publique** à `LicensingConstants.PublicKeys` (le client en accepte plusieurs) → publier un update client → basculer l'active côté serveur → révoquer l'ancienne après adoption.
- **HMAC (`AppHmacKey`)** : symétrique. Rotation = accepter 2 clés (courante + précédente) le temps de l'overlap, publier le client avec la nouvelle, puis retirer l'ancienne. *(middleware mono-clé aujourd'hui — évolution à prévoir.)*
- **Session/protocole** : durée de session (15 min) et seuils d'anomalie ajustables **côté serveur** sans update client.

### 11.4 Reste — bloqué sur des ressources externes
- **A.4 URL prod + pin SPKI** → serveur **HTTPS déployé** (+ certif TLS pour le pin).
- **A.4 HMAC prod** → injecter la clé **au build** (secret CI), pas la valeur de test.
- **D.3 signature Authenticode** → **certificat de signature de code** (le self-check est déjà prêt à mordre).
- **D.2 compilation native partielle** → gros chantier (isoler crypto/loader en natif AOT via P/Invoke).
- **D.1 fort** → obfuscateur **commercial** (control-flow, renommage public) + câblage dans le publish single-file.
- **C.3** *(optionnel)* → déport d'un calcul par requête.

---

## Annexe A — Le crack de démonstration

`dist/DataVortex-cracked.exe` (2 octets modifiés) est conservé comme **cas de test de non-régression** : après Phase D, ce chemin d'attaque doit être mort. À supprimer de toute distribution.

## Annexe B — Outils utilisés (côté attaquant)

- `ilspycmd` (ILSpy) — décompilation .NET
- Script Python maison — dépaquetage single-file bundle + patch d'octets
- `Get-AuthenticodeSignature` — vérif signature
- Tous préinstallés / triviaux à obtenir : **c'est le niveau d'effort réel de l'attaque actuelle.**

# DataVortex — client Telegram d'archivage haute performance

Application desktop Windows (**C# / .NET 8 / WPF**) qui se connecte à Telegram via l'API
utilisateur **MTProto** (WTelegramClient), surveille en temps réel des canaux choisis, télécharge
automatiquement les fichiers reçus, extrait les `*.txt` des archives, et le tout à travers un
**pipeline concurrent découplé** piloté depuis un tableau de bord temps réel.

> ⚠️ **Utilisation responsable.** N'archivez que des canaux/groupes dont vous êtes membre ou
> administrateur, et dans le respect des [conditions d'utilisation de Telegram](https://telegram.org/tos)
> et du droit applicable. Vous vous connectez avec **votre propre compte** : respectez les limites de
> débit de l'API. Cet outil est destiné à de l'archivage personnel/autorisé.

---

## Sommaire

- [Fonctionnalités](#fonctionnalités)
- [Architecture](#architecture)
- [Prérequis](#prérequis)
- [Obtenir `api_id` / `api_hash`](#obtenir-api_id--api_hash)
- [Lancer en développement](#lancer-en-développement)
- [Construire l'exécutable `.exe`](#construire-lexécutable-exe)
- [Structure des données](#structure-des-données-data)
- [Configuration (`settings.json`)](#configuration-settingsjson)
- [Choix techniques notables](#choix-techniques-notables)
- [Limites connues](#limites-connues)

---

## Fonctionnalités

**Cœur**
- Connexion userbot MTProto avec **session persistante chiffrée** (chiffrée par WTelegramClient ;
  `api_hash` protégé en plus par **DPAPI**, portée utilisateur courant).
- Écoute **événementielle** (push via `UpdateManager`, **aucun polling** de messages). L'`UpdateManager`
  récupère aussi automatiquement les updates manqués après une coupure → reconnexion sans trous.
- Filtrage : uniquement les messages **avec fichier**, uniquement les **canaux sélectionnés**.
- **Pipeline en deux étages totalement découplés** (voir [Architecture](#architecture)) :
  téléchargements et traitements tournent sur des pools de workers indépendants reliés par des
  `Channel<T>` bornés.
- Extraction `*.txt` depuis **ZIP** (`System.IO.Compression`), **RAR** et **7z** (SharpCompress), avec
  **tri sélectif par mot-clé** : par défaut seuls les `.txt` dont le **nom de fichier** contient
  « password » (insensible à la casse → `password`, `Password`, `PASSWORD`, `passwords`) sont extraits.
- Métadonnées **JSON** par fichier traité (canal source, horodatage, nom, statut, fichiers extraits…).
- Logs structurés **Serilog** (fichier journalier + flux temps réel dans l'UI).

**Interface (WPF, MVVM)**
- **Dashboard** : statut connexion, canaux surveillés, profondeur des files, téléchargements actifs,
  totaux, **graphes downloads/sec & processing/sec**, **log en direct**.
- **Channels** : liste des dialogues, cases à cocher pour choisir quoi archiver, sauvegarde.
- **Queues** : file de téléchargement (live, avec barres de progression), file de traitement (live),
  historique complet.
- **Files** : explorateur des `*.txt` extraits, recherche, « ouvrir le dossier / le fichier ».
- **Logs** : journal complet défilant.
- **Bonus** : thème **sombre/clair** commutable, **pause/reprise** du pipeline, **limiteur de bande
  passante** configurable, **retry automatique** avec back-off exponentiel.

---

## Architecture

Solution à deux projets — le cœur est **agnostique de l'UI** :

```
DataVortex.slnx
├─ src/DataVortex.Core/          (bibliothèque .NET, aucune dépendance WPF)
│  ├─ Telegram/TelegramService   – wrapper WTelegramClient (login, updates, download)
│  ├─ Pipeline/                   – DownloadPipeline, ProcessingPipeline, PipelineCoordinator,
│  │                                BandwidthLimiter, ThrottledStream, PauseGate
│  ├─ Extraction/ArchiveExtractor – ZIP / RAR / 7z → *.txt (détection par magic-bytes)
│  ├─ Storage/StorageService      – arborescence /data + métadonnées JSON
│  ├─ Metrics/MetricsService      – compteurs lock-free, snapshot 1×/s
│  ├─ Security/                   – DPAPI + CredentialStore
│  ├─ Configuration/              – AppSettings + SettingsService
│  ├─ Models/                     – DTO (jobs, records) implémentant INotifyPropertyChanged
│  └─ Abstractions/               – interfaces (DI)
│
└─ src/DataVortex.App/            (WPF, MVVM via CommunityToolkit.Mvvm)
   ├─ App.xaml(.cs)               – bootstrap DI + Serilog + cycle de vie
   ├─ Views/                      – ShellWindow, LoginDialog, 5 vues de section
   ├─ ViewModels/                 – Shell, Login, Dashboard, Channels, Queues, Files, Log
   ├─ Controls/Sparkline          – mini-graphe sans dépendance
   ├─ Converters/, Themes/        – convertisseurs + palettes Dark/Light + styles
   └─ Logging/ObservableLogSink   – sink Serilog → flux temps réel UI
```

### Le pipeline (le cœur « haute performance »)

```
  Telegram push (UpdateManager)
        │  FileDetected (canal surveillé + média document)
        ▼
  ┌───────────────┐   Channel<DownloadJob>   ┌────────────────────────┐
  │  Coordinator  │ ───────────────────────► │  DownloadPipeline       │
  └───────────────┘                          │  N workers // (3–10)    │
        ▲                                     │  bande passante + retry │
        │ OnDownloaded                        └───────────┬────────────┘
        │                                                 │ fichier prêt
        │                                     Channel<ProcessingJob>
        │                                                 ▼
        │                                     ┌────────────────────────┐
        └──────────── FileArchived ◄───────── │  ProcessingPipeline     │
                                              │  M workers // (séparés) │
                                              │  extraction *.txt + JSON│
                                              └────────────────────────┘
```

- **Découplage strict** : un fichier peut être **en cours de traitement** pendant qu'un autre se
  **télécharge encore**. Les deux étages ont leurs propres workers et leur propre `Channel<T>` borné
  (back-pressure via `BoundedChannelFullMode.Wait`).
- **Jamais de blocage de l'UI** : tout le travail est sur des threads de fond ; les `ObservableCollection`
  ne sont mutées que sur le thread UI via `IUiDispatcher`. Les objets « job » implémentent
  `INotifyPropertyChanged`, donc les cellules (statut, progression) se rafraîchissent en place.
- **Pause/reprise** : un `PauseGate` asynchrone (sans spin) que chaque worker attend avant de prendre
  l'élément suivant.
- **Limiteur de bande passante** : token-bucket global (`BandwidthLimiter`) appliqué côté écriture via
  un `ThrottledStream` ; `0` = illimité.

---

## Prérequis

- **Windows 10/11** (x64).
- **.NET 8 SDK** (ou plus récent) pour compiler. Pour exécuter le binaire publié *self-contained*,
  aucun runtime n'est requis.
- Un compte Telegram + des identifiants d'API (ci-dessous).

---

## Obtenir `api_id` / `api_hash`

1. Aller sur **https://my.telegram.org** → *API development tools*.
2. Créer une application (n'importe quel nom).
3. Noter **`App api_id`** (entier) et **`App api_hash`** (chaîne).

Ces valeurs sont saisies au premier lancement. Elles sont stockées **localement** ; le `api_hash` est
chiffré via DPAPI (`data/session/credentials.dat`), l'`api_id` et le numéro dans `data/settings.json`.

---

## Lancer en développement

```powershell
# à la racine du dépôt
dotnet restore
dotnet run --project src/DataVortex.App/DataVortex.App.csproj
```

Au premier lancement, la fenêtre de connexion demande `api_id`, `api_hash` et le numéro de téléphone
(format international, ex. `+33612345678`), puis le **code** reçu sur Telegram, et le cas échéant le
**mot de passe** de la validation en deux étapes. La session est ensuite réutilisée silencieusement.

---

## Construire l'exécutable `.exe`

Exécutable **mono-fichier, self-contained** (aucun .NET requis sur la machine cible) :

```powershell
dotnet publish src/DataVortex.App/DataVortex.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -o publish
```

Résultat : **`publish/DataVortex.exe`** (~65 Mo). Variante **framework-dependent** (plus légère, exige
le runtime .NET Desktop 8 installé) : retirer `--self-contained true` et mettre `--self-contained false`.

> 🛡️ **Note Windows — Smart App Control / contrôle d'application.** Un `.exe` fraîchement compilé et
> **non signé** peut être bloqué par *Smart App Control* (« Une stratégie de contrôle d'application a
> bloqué ce fichier »). Solutions : (a) lancer la version *framework-dependent* via `dotnet
> DataVortex.dll`, (b) signer l'exécutable avec un certificat, ou (c) autoriser le fichier dans la
> politique de sécurité. Ce n'est pas un défaut de l'application mais une politique de la machine.

---

## Structure des données (`/data`)

Créée automatiquement à côté de l'exécutable :

```
data/
├─ downloads/   fichiers bruts téléchargés ( un sous-dossier par canal : "Titre_<id>" )
├─ extracted/   *.txt extraits ( extracted/<canal>/<message_id>/… )
├─ metadata/    un .json par fichier traité (canal, horodatage, statut, fichiers extraits)
├─ logs/        journaux Serilog (rotation quotidienne, 14 jours)
└─ session/     session Telegram chiffrée + état des updates + credentials DPAPI
```

---

## Configuration (`settings.json`)

Modifiable à chaud (relancer pour les paramètres de pipeline) :

| Clé                              | Défaut | Description                                            |
|----------------------------------|:------:|-------------------------------------------------------|
| `MaxParallelDownloads`           | `4`    | Workers de téléchargement simultanés (3–10 conseillé) |
| `MaxParallelProcessing`          | `3`    | Workers de traitement simultanés                      |
| `DownloadQueueCapacity`          | `2000` | Capacité de la file de téléchargement (back-pressure) |
| `ProcessingQueueCapacity`        | `2000` | Capacité de la file de traitement                     |
| `MaxDownloadRetries`             | `3`    | Tentatives avant échec définitif                      |
| `RetryBaseDelayMs`               | `2000` | Délai de base du back-off exponentiel                 |
| `BandwidthLimitBytesPerSecond`   | `0`    | Plafond de débit (octets/s), `0` = illimité           |
| `ExtractOnlyMatchingTxt`         | `true` | N'extraire que les `.txt` dont le **nom** contient un mot-clé |
| `ExtractKeywords`                | `["password"]` | Mots-clés (sous-chaîne du nom, insensible à la casse) |
| `Theme`                          | `Dark` | `Dark` ou `Light`                                     |
| `WatchedChannels`                | `[]`   | Géré depuis l'onglet **Channels**                     |

---

## Choix techniques notables

- **UI : WPF** (et non WinUI 3). Choisi pour la **stabilité** et un *single-file publish* fiable, comme
  suggéré dans le cahier des charges (« si simplicité et stabilité maximale »).
- **Archives : SharpCompress** (et non SharpZipLib). **SharpZipLib ne sait pas lire le RAR ni le 7z** ;
  SharpCompress le fait, en **code 100 % managé** (donc compatible single-file). Le ZIP standard passe
  par `System.IO.Compression` comme demandé.
- **Concurrence : `System.Threading.Channels`** (`Channel<T>` borné) plutôt que `BlockingCollection`
  (asynchrone, non bloquant, back-pressure intégrée). TPL Dataflow a été envisagé mais `Channel<T>`
  reste plus simple et suffisant.
- **Tri par mot-clé (sur le nom de fichier)** : un `.txt` est extrait **si son nom contient** un mot-clé
  (insensible à la casse, sous-chaîne). Le **contenu n'est pas inspecté** → très efficace : une entrée dont
  le nom ne correspond pas n'est **jamais décompressée** (seul son nom, lu dans l'index de l'archive, est
  nécessaire pour décider). Configurable via `ExtractOnlyMatchingTxt` / `ExtractKeywords`.
- **Robustesse extraction** : type d'archive détecté par **magic-bytes** (puis extension) ; protection
  contre le *zip-slip* (chaque entrée est aplatie vers son nom de fichier) ; les octets sont copiés
  **tels quels** (aucun ré-encodage → tolérant aux encodages exotiques) ; toute erreur d'archive
  corrompue est **capturée**, jamais propagée.
- **Reconnexion** : WTelegramClient se reconnecte seul ; une sonde de keep-alive (`Help_GetConfig`,
  60 s — *pas* du polling de messages) reflète l'état dans l'UI et l'`UpdateManager` rattrape les
  messages manqués.
- **DI légère** : `Microsoft.Extensions.DependencyInjection` (pas d'hôte générique complet).

---

## Limites connues

- Le `file_reference` d'un document Telegram peut expirer ; un retry sur un très vieux job réutilise la
  même référence. Pour des archives de gros volume en léger différé c'est sans incidence ; une évolution
  possible serait de re-résoudre le message avant un retry tardif.
- L'extraction ne sort **que des `*.txt`** (par conception). Adapter `ArchiveExtractor.IsTxt` /
  `DetectKind` pour d'autres types.
- La connexion Telegram réelle nécessite vos identifiants : elle n'a pas pu être testée de bout en bout
  en environnement d'intégration (le code est néanmoins compilé directement contre l'assembly
  WTelegramClient 4.4.5, donc l'API utilisée est validée).

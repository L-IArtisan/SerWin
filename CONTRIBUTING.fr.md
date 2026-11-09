# Contribuer à SerWin

🇬🇧 [English version](CONTRIBUTING.md)

Merci de ton intérêt ! La contribution la plus précieuse est **la base de connaissances** : signaler une explication fausse, compléter une fiche, en écrire une pour un service qui n'en a pas.

## Signaler un problème

Utilise les [modèles de tickets](https://github.com/L-IArtisan/SerWin/issues/new/choose) :

- **Fiche incorrecte** — une explication fausse, incomplète ou dépassée ;
- **Nouvelle fiche** — un service sans fiche (`SerWin.Verify coverage` les liste) ;
- **Bug** — un comportement anormal de l'application.

Indique toujours ton édition et ton build de Windows (affichés en bas de la fenêtre de SerWin).

## Compiler et lancer

Prérequis : Windows 11, [SDK .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build
dotnet test
dotnet run --project src/SerWin.App
```

Exécutables portables (un seul fichier chacun, .NET inclus) :

```powershell
dotnet publish src/SerWin.App -c Release -r win-x64 -o publish
dotnet publish src/SerWin.Verify -c Release -r win-x64 -o publish   # outil de vérification, utilisable dans Windows Sandbox
```

Les tests qui modifient réellement un service (le service Fax, remis dans son état exact) ne s'exécutent que depuis un terminal administrateur ; sinon ils sont signalés « ignorés ».

### Structure

```
src/SerWin.App/        Interface WPF (WPF-UI, style Fluent)
src/SerWin.Core/       Accès SCM (advapi32), registre, photographie, actions, listes de sécurité
src/SerWin.Knowledge/  Chargement et validation de la base de connaissances
src/SerWin.Verify/     Outil CLI de vérification des fiches
knowledge/             Base de connaissances (un JSON par service ou processus) — CC BY-SA 4.0
tests/                 Tests xUnit
tools/                 Scripts (génération des .resx, manifeste winget)
packaging/winget/      Modèles de manifeste winget
```

## Écrire ou corriger une fiche

Chaque service est décrit dans `knowledge/services/<Nom>.json` (schéma : [`knowledge/schema.json`](knowledge/schema.json)). Une fiche contient :

- trois rédactions — **Simple** (pour tout le monde), **Avancé** (mécanismes), **Expert** (DLL, groupe svchost, compte, registre) — en français (langue source) et en anglais ;
- les conséquences d'une désactivation (`ifDisabled`) ;
- les mécanismes de relance connus (`revivers`) ;
- les règles de contexte (`usefulIf`, ex. `{ "bluetooth": "present" }`) et le verdict si la règle n'est pas remplie.

Règles de rédaction :

- **tutoyer** en français, phrases courtes, pas de jargon au niveau Simple ;
- ne jamais affirmer ce qu'on n'a pas vérifié : un doute → le dire ;
- un service `critique` doit figurer dans la liste rouge codée en dur (`SafetyLists.cs`), un test le vérifie.

```powershell
dotnet run --project src/SerWin.Verify -- validate knowledge   # valider toutes les fiches
dotnet run --project src/SerWin.Verify -- dump wuauserv         # configuration réelle d'un service
dotnet run --project src/SerWin.Verify -- coverage              # services de ta machine sans fiche
dotnet run --project src/SerWin.Verify -- who wuauserv          # qui peut le relancer, qui l'a relancé
dotnet run --project src/SerWin.Verify -- compare               # écarts avec les réglages d'usine connus
```

### Niveaux de vérification

- 📝 **Brouillon** — rédigé à partir de la documentation publique ;
- 🔍 **Configuration vérifiée** — déclencheurs, dépendances et compte confirmés sur un build précis via `SerWin.Verify` ;
- ✅ **Comportement observé** — désactivation testée, relances constatées.

Pour passer des fiches en « 🔍 Configuration vérifiée » :

1. Dans **Windows Sandbox** (machine vierge, détectée automatiquement) : `SerWin.Verify.exe capture` → produit `serwin-capture-<Édition>-<build>.json`.
2. Dans le dépôt : `dotnet run --project src/SerWin.Verify -- apply serwin-capture-….json` (`--dry-run` pour simuler).

`apply` enregistre la configuration d'usine (`defaults["Édition:Build"]`) et ne marque une fiche vérifiée que si tout ce qu'elle affirme existe réellement. Une capture faite sur une machine déjà modifiée ne reporte pas les types de démarrage.

### Destinations réseau (v1.1)

Chaque service en ligne est décrit dans `knowledge/destinations/<nom>.json` (schéma : [`knowledge/destination.schema.json`](knowledge/destination.schema.json)) : motifs de noms d'hôtes (`login.live.com`, `*.events.data.microsoft.com`), éditeur, catégorie, utilité (`essentiel`, `utile`, `optionnel`, `indesirable`), programmes émetteurs habituels, ce qui casse si on la bloque et le réglage officiel qui la réduit. Une fiche reste **générale** (vraie pour tout PC sous Windows 11) et ne décrit jamais une machine particulière. Entre deux fiches, le motif le plus précis l'emporte.

```powershell
dotnet run --project src/SerWin.Verify -- net              # ce que ce PC échange (journal DNS + TCP en direct), avec les fiches
dotnet run --project src/SerWin.Verify -- net --unknown    # noms d'hôtes vus ici sans fiche
```

## Traduire l'interface

Les chaînes d'interface sont dans `src/SerWin.App/Resources/Strings.tsv` (clé, français, anglais). Après modification :

```powershell
powershell -ExecutionPolicy Bypass -File tools\Build-Resx.ps1
```

Un test vérifie que chaque clé utilisée existe dans les deux langues.

## Principes à respecter

- **Zéro réseau** : aucune fonctionnalité ne doit se connecter à Internet.
- **Comprendre avant d'agir** : pas d'action de masse sans explication, rien de coché d'office.
- **Tout est annulable** : toute nouvelle action passe par `ActionEngine` (sauvegarde → vérification → historique).

## Publier une version

1. Mettre à jour `<Version>` dans `Directory.Build.props`, puis pousser une étiquette : `git tag v1.2.3 && git push origin v1.2.3`.
2. `.github/workflows/release.yml` compile, valide la base, lance les tests, publie `SerWin.exe` et `SerWin.Verify.exe`, les signe si SignPath est configuré (secrets `SIGNPATH_API_TOKEN`, `SIGNPATH_ORGANIZATION_ID`), et crée la release avec `SHA256SUMS.txt`.
3. winget : voir [`packaging/winget/README.md`](packaging/winget/README.md).

## Licences

En contribuant, tu acceptes que ton code soit publié sous [GPL-3.0-or-later](LICENSE) et tes fiches sous [CC BY-SA 4.0](knowledge/LICENSE).

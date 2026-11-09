# SerWin

> Comprendre, gérer et reprendre la main sur les services et processus Windows — en connaissance de cause.

SerWin explique ce que fait chaque service Windows, ce qui casse si on le coupe, et **pourquoi un service désactivé redémarre**. Il permet ensuite d'agir en sécurité : sauvegarde systématique, historique, annulation.

**Principe directeur : comprendre avant d'agir.** Pas de profil « optimisation » en un clic, pas d'action de masse aveugle.

## 🔒 Engagement : zéro réseau

**SerWin ne se connecte jamais à Internet.** Aucune télémétrie, aucune statistique d'usage, aucune vérification de mise à jour, aucun appel à un service d'IA. La base de connaissances est intégrée à l'exécutable.

Toutes les données que SerWin écrit restent sur ta machine, en JSON lisible :

| Emplacement | Contenu |
|---|---|
| `%ProgramData%\SerWin\snapshot.json` | Photographie de l'état des services prise au premier lancement |
| `%ProgramData%\SerWin\history.json` | Historique de toutes les modifications (chacune annulable) |
| `%ProgramData%\SerWin\backups\*.json` | Sauvegarde complète (config SCM, clé de registre, ACL) avant chaque modification |
| `%AppData%\SerWin\settings.json` | Tes préférences (langue, niveau) |

En mode administrateur, SerWin réserve l'écriture de `%ProgramData%\SerWin\` aux administrateurs (lecture pour tous), pour qu'un compte standard ne puisse pas falsifier une sauvegarde.

## État d'avancement

| Jalon | Contenu | État |
|---|---|---|
| **v0.1 — Lecture seule** | Liste des services, recherche, filtres, onglets *Comprendre* / *Technique*, détection édition/build, niveaux Simple/Avancé/Expert, 46 fiches FR/EN, photographie au premier lancement | ✅ |
| **v0.2 — Agir en sécurité** | Démarrer / arrêter / type de démarrage, élévation, sauvegarde, historique, annulation, Tout restaurer, listes rouge et orange, point de restauration, export / suppression des données | ✅ |
| **v0.3 — Qui le relance** | Analyse statique (7 familles de mécanismes), chronologie corrélée (7040, plantages, tâches, mises à jour), alertes « désactivé puis relancé », journal du Planificateur, `SerWin.Verify` complet | ✅ |
| **v0.4 — Processus** | Arbre parent → enfant, svchost dépliés, signatures Authenticode (hors ligne), emplacements suspects, consommation CPU / RAM / disque par service, 38 fiches processus, terminer (liste rouge) | ✅ |
| **v0.5 — Chez toi** | Détection du contexte (15 critères, locale et sans réseau), verdict « utile / inutile chez toi », écran de suggestions à cocher une par une (+ vie privée), comparaison avec les réglages d'usine | ✅ |
| **v0.6 — Expert** | Neutralisation complète (désactivation + tâches liées + déclencheurs + récupération, aperçu détaillé, annulable à l'octet près), verrouillage ACL de la clé de registre, écran d'accueil par profil | ✅ |
| **v1.0 — Publication** | 196 fiches services FR/EN (dont tous les services des listes rouge et orange), interface entièrement traduite, publication automatisée (GitHub Releases, signature SignPath), manifeste winget | ✅ (fiches en 📝 brouillon, signature à activer) |

Voir [SPEC.md](SPEC.md) pour le cahier des charges complet.

## Installer

- **Portable** : télécharger `SerWin-vX.Y.Z-win-x64.exe` depuis les [Releases](../../releases) et le lancer. Rien à installer, .NET inclus. `SHA256SUMS.txt` permet de vérifier le fichier (`Get-FileHash SerWin-*.exe`).
- **winget** (une fois le manifeste accepté) : `winget install LIArtisan.SerWin`.

Tant que l'exécutable n'est pas signé, Windows SmartScreen affiche « Windows a protégé votre ordinateur » au premier lancement : *Informations complémentaires* → *Exécuter quand même*.

## Compiler et lancer

Prérequis : Windows 11, [SDK .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build
dotnet test
dotnet run --project src/SerWin.App
```

Exécutables portables (un seul fichier chacun, sans installation de .NET) :

```powershell
dotnet publish src/SerWin.App -c Release -r win-x64 -o publish
dotnet publish src/SerWin.Verify -c Release -r win-x64 -o publish   # outil de vérification, utilisable dans Windows Sandbox
```

Aucun droit administrateur n'est nécessaire pour consulter. Pour modifier, le bouton 🛡️ relance SerWin en administrateur.

Les tests qui modifient réellement un service (le service Fax, remis dans son état exact) ne s'exécutent que depuis un terminal administrateur ; sinon ils sont signalés « ignorés ».

## Structure

```
src/SerWin.App/        Interface WPF (WPF-UI, style Fluent)
src/SerWin.Core/       Accès SCM (advapi32), registre, photographie, listes de sécurité
src/SerWin.Knowledge/  Chargement et validation de la base de connaissances
src/SerWin.Verify/     Outil CLI de vérification des fiches
knowledge/             Base de connaissances (un JSON par service) — CC BY-SA 4.0
tests/                 Tests xUnit
tools/                 Scripts (génération des .resx)
```

## Contribuer à la base de connaissances

Chaque service est décrit dans `knowledge/services/<Nom>.json` (schéma : [`knowledge/schema.json`](knowledge/schema.json)). Une fiche contient trois rédactions (Simple, Avancé, Expert) en français et en anglais, les conséquences d'une désactivation et les mécanismes de relance connus.

Niveaux de vérification :

- 📝 **Brouillon** — rédigé à partir de la documentation publique ;
- 🔍 **Configuration vérifiée** — triggers, dépendances et compte confirmés sur un build précis via `SerWin.Verify` ;
- ✅ **Comportement observé** — désactivation testée, relances constatées.

```powershell
# Valider toutes les fiches
dotnet run --project src/SerWin.Verify -- validate knowledge
# Voir la configuration réelle d'un service sur ta machine
dotnet run --project src/SerWin.Verify -- dump wuauserv
# Lister les services de ta machine qui n'ont pas encore de fiche
dotnet run --project src/SerWin.Verify -- coverage
# Qui pourrait relancer un service, et qui l'a réellement relancé
dotnet run --project src/SerWin.Verify -- who wuauserv
# Écarts entre ta machine et les réglages d'usine connus
dotnet run --project src/SerWin.Verify -- compare
```

**Passer des fiches en « 🔍 Configuration vérifiée »** :

1. Dans **Windows Sandbox** (machine vierge, détectée automatiquement), en administrateur :
   `SerWin.Verify.exe capture` → produit `serwin-capture-<Édition>-<build>.json`.
2. Dans le dépôt : `dotnet run --project src/SerWin.Verify -- apply serwin-capture-….json` (ajouter `--dry-run` pour simuler).

`apply` enregistre la configuration d'usine (`defaults["Édition:Build"]`) et ne marque une fiche vérifiée que si tout ce qu'elle affirme (services réparateurs, tâches, déclencheurs) existe réellement. Une capture faite sur une machine déjà modifiée ne reporte pas les types de démarrage, pour ne jamais enregistrer des réglages d'« optimiseur » comme réglages d'usine.

Les chaînes d'interface se traduisent dans `src/SerWin.App/Resources/Strings.tsv` (clé, français, anglais), puis :

```powershell
powershell -File tools\Build-Resx.ps1
```

## Publier une version

1. Mettre à jour `<Version>` dans `Directory.Build.props`, puis pousser une étiquette : `git tag v1.0.0 && git push origin v1.0.0`.
2. `.github/workflows/release.yml` compile, valide la base, lance les tests, publie `SerWin.exe` et `SerWin.Verify.exe`, les signe si SignPath est configuré, et crée la release GitHub avec `SHA256SUMS.txt`.
3. **Signature** : demander l'adhésion gratuite à [SignPath Foundation](https://signpath.org) (projets open source), créer le projet `serwin` (politique `release-signing`, configuration d'artefact `portable-exe`), puis ajouter les secrets `SIGNPATH_API_TOKEN` et `SIGNPATH_ORGANIZATION_ID` au dépôt. Sans ces secrets, la release est publiée non signée.
4. **winget** : voir [`packaging/winget/README.md`](packaging/winget/README.md).

## Licences

- Code : [GPL-3.0](LICENSE)
- Base de connaissances (`knowledge/`) : [CC BY-SA 4.0](knowledge/LICENSE)

Les contributions externes sont soumises à un CLA (à venir).

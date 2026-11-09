# Manifeste winget

Modèles pour [winget-pkgs](https://github.com/microsoft/winget-pkgs) (type `portable` : winget copie `SerWin.exe` et crée la commande `serwin`).

Après une publication GitHub (`.github/workflows/release.yml`) :

```powershell
powershell -ExecutionPolicy Bypass -File tools\New-WingetManifest.ps1 -Version 1.0.0 
```

Le script ne se connecte à rien : il lit `SHA256SUMS.txt` que tu as récupéré de la release (ou calcule l'empreinte d'un `SerWin.exe` local avec `-Exe`), remplit les modèles dans `packaging/winget/out/<version>/`, puis :

```powershell
winget validate packaging\winget\out\1.0.0
winget install --manifest packaging\winget\out\1.0.0   # test local
```

Soumission : copier le dossier dans un fork de winget-pkgs sous `manifests/l/LIArtisan/SerWin/<version>/` et ouvrir une pull request (ou `wingetcreate submit`).

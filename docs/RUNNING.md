# Installation et lancement

1. `dotnet publish src/MyWhoosh2Garmin -c Release -r osx-arm64 --self-contained -o publish/`
   (utiliser `osx-x64` au lieu de `osx-arm64` si le Mac n'est pas Apple Silicon)
2. `sudo cp publish/MyWhoosh2Garmin /usr/local/bin/mywhoosh2garmin`
3. `/usr/local/bin/mywhoosh2garmin login` (une seule fois, ou à chaque révocation du compte Garmin)
4. `cp packaging/com.mywhoosh2garmin.plist ~/Library/LaunchAgents/`
5. `launchctl load ~/Library/LaunchAgents/com.mywhoosh2garmin.plist`

Le service tourne désormais en tâche de fond et démarre automatiquement à la connexion.

Pour l'arrêter : `launchctl unload ~/Library/LaunchAgents/com.mywhoosh2garmin.plist`

Logs : `/tmp/mywhoosh2garmin.log` et `/tmp/mywhoosh2garmin.error.log`.

## Vérification manuelle après installation

Ces étapes nécessitent un vrai compte Garmin et une vraie séance MyWhoosh — à faire toi-même :

1. Lance `mywhoosh2garmin login`, entre ton email/mot de passe Garmin réels.
   Si ça échoue avec une `GarminSsoException`, le message précisera si Garmin a rejeté
   les identifiants ou si le format de réponse ne correspond pas à ce qui était attendu
   (l'API étant non-officielle, elle peut changer).
2. Vérifie que le jeton est bien dans le Keychain :
   `security find-generic-password -s MyWhoosh2Garmin-GarminOAuth1 -w`
3. Lance `mywhoosh2garmin` (sans argument) dans un terminal que tu peux observer,
   fais une courte séance MyWhoosh (même une minute suffit), et termine-la.
   Une notification macOS "Activité synchronisée avec Garmin ✅" doit apparaître
   quelques secondes après, et l'activité doit être visible dans Garmin Connect.

Si l'upload échoue, le message de log indiquera quelle hypothèse (le format JSON exact
de la réponse Garmin, dans `GarminUploadClient`) ne correspond pas à la réalité — c'est
le seul endroit du projet où un ajustement basé sur la vraie réponse du serveur est attendu.

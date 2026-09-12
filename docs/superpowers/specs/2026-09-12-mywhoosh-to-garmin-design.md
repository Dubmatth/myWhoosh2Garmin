# MyWhoosh → Garmin Connect auto-sync — Design

Date: 2026-09-12
Statut: Approuvé pour passage en plan d'implémentation

## Contexte et objectif

Aujourd'hui, l'utilisateur démarre sa montre Garmin séparément de MyWhoosh (application de cyclisme indoor) : deux enregistrements déconnectés, et aucune donnée MyWhoosh n'atterrit dans Garmin Connect.

Objectif : reproduire le comportement de Zwift, qui synchronise automatiquement chaque activité vers Garmin Connect sans que l'utilisateur ait besoin de démarrer une activité sur sa montre. Le projet est développé par l'utilisateur lui-même en C#/.NET, à des fins d'apprentissage, plutôt que d'utiliser un des projets open source existants (tous en Python).

## Décision d'architecture : watcher de fichier local

Deux approches ont été évaluées :

- **Option A (retenue) — Watcher de fichier local** : surveille le dossier où MyWhoosh écrit ses fichiers `.fit`, détecte les nouvelles activités, les upload vers Garmin.
- **Option B (écartée) — Appel à l'API MyWhoosh non-officielle** : nécessiterait une deuxième authentification non-officielle (en plus de celle de Garmin) et reposerait sur une API propriétaire non documentée, plus fragile qu'un format de fichier standard.

Option A retenue car : une seule surface d'authentification non-officielle à maintenir (Garmin), format `.fit` stable et documenté (SDK officiel Garmin disponible pour le lire), réaction immédiate sans polling, et c'est l'approche validée par le projet open source le plus mature du domaine (JayQueue/MyWhoosh2Garmin).

## Emplacement des fichiers MyWhoosh

- macOS : `~/Library/Containers/com.whoosh.whooshgame/Data/Library/Application Support/Epic/MyWhoosh/Content/Data`
- Motif de nom de fichier : `MyNewActivity-*.fit`

## Architecture

Service .NET 10 (LTS), structuré autour de `Microsoft.Extensions.Hosting` (Generic Host + `BackgroundService`), packagé comme app console autonome lancée via un `LaunchAgent` macOS (démarrage automatique à la connexion).

Quatre composants :

1. **Watcher** — surveille le dossier MyWhoosh via `FileSystemWatcher`, avec un fallback en polling léger en complément (macOS peut manquer des événements `FileSystemWatcher` dans certains cas).
2. **Auth Garmin** — gère le flux SSO Garmin (reverse-engineered, façon lib Python `garth`, aucun équivalent C# mature n'existe) : login interactif au premier lancement (email/mot de passe Garmin), échange contre des tokens OAuth2, stockage sécurisé et refresh automatique.
3. **Uploader** — envoie le fichier `.fit` détecté à l'endpoint d'upload Garmin Connect avec le token Bearer.
4. **Tracker d'état** — mémorise les fichiers déjà uploadés pour éviter tout doublon, y compris après un redémarrage du service.

## Détail des composants

### Auth Garmin

- Login interactif unique (email/mot de passe Garmin saisis au premier lancement).
- Flux reproduisant `garth` : `sso.garmin.com/sso/signin` → ticket → échange OAuth2 via `connectapi.garmin.com`.
- Tokens (accès + refresh) stockés dans le **Keychain macOS** (via la commande `security`), jamais en fichier clair.
- Refresh automatique et transparent tant que le refresh token est valide ; nouvelle saisie du mot de passe seulement si le compte est révoqué côté Garmin.

### Uploader

- Envoi en `multipart/form-data` vers l'endpoint d'upload Garmin Connect, avec le token Bearer en en-tête `Authorization`.
- Trois cas de réponse gérés : succès, doublon (activité déjà présente — traité comme un succès, pas une erreur), erreur d'auth (déclenche un refresh de token puis un retry unique).

### Tracker d'état

- Fichier SQLite local.
- Enregistre pour chaque fichier traité : nom, hash, ID d'activité Garmin renvoyé, statut (traité / en attente de retry).
- Consulté avant tout upload pour garantir l'idempotence.

## Flux de données

1. Fin de séance → MyWhoosh écrit le `.fit` dans son dossier.
2. Le watcher détecte le nouveau fichier et attend qu'il soit stable (taille inchangée pendant ~2s) avant de le traiter, pour éviter de lire un fichier en cours d'écriture.
3. Vérification dans le tracker que ce fichier n'a pas déjà été traité.
4. Vérification/refresh du token Garmin si nécessaire.
5. Upload du fichier.
6. Marquage du fichier comme traité dans le tracker, log du résultat, notification macOS native ("Activité synchronisée avec Garmin ✅").

## Gestion des erreurs

| Scénario | Comportement |
|---|---|
| Fichier partiellement écrit | Géré par le contrôle de stabilité (étape 2 du flux) avant tout traitement |
| Token Garmin expiré | Refresh automatique ; en cas d'échec du refresh, notification claire à l'utilisateur, le service continue de tourner |
| Panne réseau lors de l'upload | 3 tentatives avec backoff, puis le fichier reste marqué "en attente" dans le tracker et sera retenté au cycle/démarrage suivant |
| MyWhoosh change son dossier de stockage (mise à jour de l'appli) | Détection au démarrage si le dossier configuré n'existe plus, log explicite plutôt qu'un échec silencieux permanent |

## Tests

- Tests unitaires sur la logique pure : dédoublonnage (tracker), détection de stabilité de fichier, refresh de token (HTTP mocké) — sans dépendance à un vrai compte Garmin ni à de vrais fichiers MyWhoosh.
- Tests du watcher avec un dossier temporaire simulant l'apparition/écriture progressive de fichiers.
- Validation manuelle finale avec le compte Garmin réel de l'utilisateur, sur une activité de test, avant mise en service définitive.
- Approche TDD (tests écrits avant le code) pour l'implémentation.

## Hors périmètre (YAGNI)

- Support Windows/Linux (le service cible macOS uniquement pour l'instant ; l'architecture .NET reste cross-platform si besoin futur).
- Fusion des données de la montre Garmin (fréquence cardiaque) avec celles de MyWhoosh — l'objectif est de remplacer l'usage de la montre pour ces séances, pas de fusionner deux sources.
- Interface graphique — le service tourne uniquement en tâche de fond, sans UI.

# MyWhoosh2Garmin

Service macOS en arrière-plan qui synchronise automatiquement les activités [MyWhoosh](https://www.mywhoosh.com/) vers Garmin Connect — sans jamais avoir besoin de démarrer une activité sur la montre. L'objectif : reproduire pour MyWhoosh le comportement natif de Zwift, qui pousse automatiquement chaque sortie vers Garmin Connect.

Écrit en C# / .NET 10 comme projet d'apprentissage personnel.

## Comment ça marche

1. Un `BackgroundService` surveille en continu le dossier local où MyWhoosh écrit ses fichiers `.fit` à la fin de chaque activité.
2. Dès qu'un nouveau fichier stable est détecté, il est comparé à une base locale (SQLite) pour éviter tout doublon.
3. Le service s'authentifie auprès de Garmin (flux mobile non-officiel, jetons mis en cache dans le Keychain macOS) et uploade le fichier vers Garmin Connect.
4. Une notification macOS confirme la synchronisation.

Garmin n'ayant pas d'API publique pour les particuliers, l'authentification et l'upload reproduisent le flux mobile utilisé par la référence communautaire [`garth`](https://github.com/matin/garth) (Python) — voir la section Limitations ci-dessous.

## Prérequis

- macOS (Apple Silicon ou Intel)
- [.NET SDK 10](https://dotnet.microsoft.com/download) ou plus récent
- Un compte Garmin Connect
- L'application MyWhoosh installée et déjà utilisée au moins une fois (pour que son dossier de données existe)

## Installation

```bash
git clone <url-du-repo>
cd myWhoosh2Garmin
dotnet publish src/MyWhoosh2Garmin -c Release -r osx-arm64 --self-contained -o publish/
# utiliser -r osx-x64 sur un Mac Intel
sudo cp publish/MyWhoosh2Garmin /usr/local/bin/mywhoosh2garmin
```

Lier ton compte Garmin (une seule fois) :

```bash
mywhoosh2garmin login
```

Si ton compte a la double authentification (MFA) activée, le code reçu par email/SMS te sera demandé automatiquement.

Démarrage automatique en tâche de fond (LaunchAgent macOS) :

```bash
cp packaging/com.mywhoosh2garmin.plist ~/Library/LaunchAgents/
launchctl load ~/Library/LaunchAgents/com.mywhoosh2garmin.plist
```

Instructions détaillées, logs et vérification manuelle : [`docs/RUNNING.md`](docs/RUNNING.md).

## Développement

```bash
dotnet test    # suite de tests unitaires (xUnit)
dotnet build
```

Le projet suit le TDD : chaque composant réseau/système (HTTP Garmin, Keychain, processus externes) est isolé derrière une interface injectable et testé avec des doublures écrites à la main plutôt qu'avec un framework de mock.

Documentation de conception : [`docs/superpowers/specs/`](docs/superpowers/specs/) (spec d'architecture) et [`docs/superpowers/plans/`](docs/superpowers/plans/) (plan d'implémentation détaillé).

## Limitations connues

**Métriques d'entraînement Garmin incomplètes.** Les métriques calculées par l'algorithme propriétaire Firstbeat de Garmin (Puissance Normalisée, Intensity Factor, Training Stress Score, Training Effect aérobie/anaérobie, travail total) sont calculées **sur la montre elle-même**, jamais par les serveurs Garmin Connect. Le fichier `.fit` généré par MyWhoosh ne contient pas ces champs, contrairement à un fichier enregistré directement par une montre Garmin. Comparaison vérifiée sur une vraie séance :

| Donnée | Montre Garmin | Via MyWhoosh2Garmin |
|---|---|---|
| Fréquence cardiaque, puissance, cadence | ✅ | ✅ identique |
| Distance, durée, calories | ✅ | ⚠️ calculées différemment (MyWhoosh simule sa propre route virtuelle) |
| Puissance Normalisée / TSS / Intensity Factor / Training Effect | ✅ | ❌ absent |

Certaines apps tierces certifiées "TrueUp" par Garmin (Zwift et Rouvy confirmés) bénéficient d'un mécanisme où l'activité est redescendue vers une montre compatible à la prochaine synchro, traitée par son Firstbeat, puis remontée complète vers Garmin Connect. Il n'est **pas confirmé** que MyWhoosh fasse partie de cette liste — à vérifier au cas par cas en regardant si le Training Effect apparaît a posteriori sur l'activité synchronisée. Si l'exhaustivité de ces métriques est critique pour toi, enregistrer directement sur la montre reste la seule option garantie.

**VO2 max jamais mis à jour** pour une activité de cyclisme indoor/virtuel, quelle que soit la source (limitation Garmin universelle, sans rapport avec cet outil).

**Authentification MFA limitée à un code (email/SMS).** Si ton compte utilise une méthode MFA différente (ex. approbation par notification push dans l'app Garmin Connect plutôt qu'un code à saisir), la commande `login` ne la gèrera pas.

**API Garmin non-officielle.** Aucune API publique n'existe pour ce cas d'usage : l'authentification et l'upload reproduisent le flux mobile interne de Garmin (reverse engineering, comme le fait la référence Python `garth`). Garmin peut modifier ce flux sans préavis et casser le service ; utilisation à tes risques vis-à-vis des conditions d'utilisation de Garmin.

**macOS uniquement**, avec un chemin d'installation MyWhoosh en dur. Si une mise à jour de MyWhoosh change cet emplacement, le service le détecte et le journalise comme erreur au démarrage plutôt que de planter silencieusement, mais nécessite alors une mise à jour du code.

## Licence

À définir.

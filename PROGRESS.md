/# Ghost of Yōtei — boot vers le menu (branche fix/yotei-boot-deadlock)

## 2026-07-17 — Session en cours

### État observé (preuves, pas de mémoire)

Le blocage actuel n'est **pas** le gel FaWorkerIo1 : c'est un **crash natif avant
toute présentation** (exit 139 = AV 0xC0000005 non rattrapé, banner
`NATIVE EXCEPTION CAUGHT` dans le log). Aucune activité VideoOut/flip dans les
logs des runs actuels — le splash n'est plus atteint. L'assert Scream était déjà
présent hier soir (`log_yotei_snap4.txt`, 2 occurrences) : ce n'est pas une
régression d'aujourd'hui.

Exit codes vérifiés dans le source (règle 6) :
- `4`  = watchdog de stall, `DirectExecutionBackend.cs:5666` (« Execution stalled with no import progress »)
- `0`  = fermeture fenêtre VideoOut (`VideoOutExports.cs:166`)
- `139` = AV natif (128+11 côté shell), correspond au banner 0xC0000005
- `6`  = absent de notre code — vraisemblablement un `exit(6)` du jeu via HLE

### Étape 0 — A/B régression

- **Run (a)** HEAD c9fd4fa + stubs non commités (ACM batch, AJM batch,
  GetQueueLevel headroom) : ×2 runs (`log_yotei_menu1.txt`, `log_yotei_menu2.txt`).
  Point d'arrêt reproductible : import #29549 (#29033 au 2e run, même site
  `ret=0x8002561F4`), assert Scream `snd_osfuncs.cpp:408`
  « scePthreadMutexUnlock FAILED w/ error -2147352573 » (= 0x80020003,
  ORBIS_GEN2_ERROR_INVALID_ARGUMENT), puis AV à RIP 0x80025621F (lecture
  0xFFFFFFFFFFFFFFFF dans le chemin d'erreur du jeu) → exit 139.
  Aucun flip/present avant le crash.
- **Run (b)** idem avec les stubs audio non commités stashés (`log_yotei_ab_b.txt`) :
  **gel** (pas de crash) — dernier import logué #23858, log figé à 12 864 lignes,
  1 076 s de CPU consommées en 18 min = busy-spin. Spam EDEADLK sur le mutex
  connu 0x804C4B9B8 (chemin FaWorkerIo1). Aucun flip/present. Même 6 échecs
  create_shader type=5.

**Verdict A/B : on garde (a).** Les stubs audio font passer le mur de la pompe
NCA (~#23858 → ~#29549, ~6 000 imports de plus). La perte du splash n'est pas
imputable aux stubs : aucune des deux configs ne présente de frame, et l'assert
Scream existait déjà dans `log_yotei_snap4.txt` d'hier soir.

### Étape 2 — Diagnostic écrit du crash (cause racine identifiée, trace à l'appui)

Trace `log_yotei_menu2.txt` (SHARPEMU_LOG_PTHREADS=1), séquence finale sur le
thread principal (managed=2) :

```
pthread_lock:   mutex=0x7FFFF01FF890 resolved=0x600000008780  guest[0]=0x600000008780  rec=1 OK
pthread_unlock: mutex=0x7FFFF01FF898 resolved=0x7FFFF01FF898  guest[0]=0x600000008780  rec=0 OK
pthread_unlock: mutex=0x7FFFF01FF898 resolved=0x7FFFF01FF898  guest[0]=0x6000000088C0  rec=0 result=0x80020003  ← ÉCHEC
```

- Scream (`snd_osfuncs.cpp`) copie les **handles** de mutex (`ScePthreadMutex`,
  8 octets) dans des locales de pile : le même slot de pile (0x7FFFF01FF898)
  est réutilisé d'un appel à l'autre avec des handles **différents**
  (0x600000008780 puis 0x6000000088C0).
- `TryResolveMutexState` (KernelPthreadCompatExports.cs:916) consulte le cache
  `_mutexStates` **indexé par adresse** avant de relire le handle en mémoire
  guest ; l'entrée périmée `0x7FFFF01FF898 → état(8780)` gagne alors que le slot
  contient désormais 88C0. Le mutex 88C0 est bien verrouillé (rec=1, cf. trace
  `lock mutex=0x7FFFF01FF910 resolved=0x6000000088C0`), mais l'unlock est routé
  vers l'état de 8780 (rec=0) → INVALID_ARGUMENT.
- Le jeu traite l'échec comme fatal : assert + chemin d'erreur qui AV.

Sur hardware réel, l'identité d'un mutex est le handle contenu dans l'objet,
jamais l'adresse du pointeur. Fix minimal : résolution « contenu d'abord »
(lire guest[0] ; si le handle est enregistré, l'utiliser ; l'adresse ne sert
que de repli), même réordonnancement dans `ResolveMutexHandle`.

Le même motif de cache existe pour `_condStates` (cond vars) — non observé en
échec, non touché (une cause, un fix).

### Blocage secondaire connu (non traité dans ce fix)

`sceAgcCreateShader` (f3dg2CSgRKY) rejette les shaders **type=5 (hull shader)** :
registres PGM `lo=0x10A hi=0x10B` absents de la table de
`PatchShaderProgramRegisters` (AgcExports.cs:10025). Motif cohérent avec les
constantes existantes (PS=0x8, GS=0x8A, ES=0xC8, LS=0x148 → HS=0x10A).
6 échecs par run, **non fatals** (le jeu continue). Prochaine hypothèse après le
fix mutex.

### Étape 3 — Fix appliqué et vérifié

Fix : résolution « contenu d'abord » dans `TryResolveMutexState` et
`ResolveMutexHandle` (KernelPthreadCompatExports.cs) — le handle lu dans
l'objet guest prime sur le cache indexé par adresse ; l'entrée d'adresse est
rafraîchie à chaque résolution par contenu.

Run de vérification (`log_yotei_mutexfix1.txt`, SHARPEMU_LOG_PTHREADS=1) :
- 0 échec `pthread_unlock`, 0 assert Scream (avant : crash reproductible ×2) ;
- progression #29549 → **#32415+** (~3 000 imports plus loin) ;
- tests unitaires mutex/pthread : 2/2 OK, build 0 erreur.

### NOUVEAU MUR (documenté, non traité — arrêt conformément au protocole)

Exit 139, AV **écriture à NULL** à RIP 0x807567218 = `Q3VBxCXhUHs+0x648`,
une fonction de la **libc bundlée** (LLE redirect logué au boot :
`0x0000700000001970 Q3VBxCXhUHs -> 0x807566BD0`). Registres au crash :
rdi=0 (destination NULL), rdx=0x24A, rax=0 — allure de memset/memcpy vers NULL.

Chaîne en amont (thread principal, même fonction du jeu 0x801612xxx) :
1. Import#32415 `6lNcCp+fxi4` appelé avec **rdi=0 rsi=0** rdx=0xE0008000 →
   INVALID_ARGUMENT (ret=0x80161279F) — le jeu a DÉJÀ un pointeur/valeur nul ici.
2. Retour à 0x8016127C4 (rsp+0x08) → appel libc `Q3VBxCXhUHs` avec le même
   NULL → AV.

Le NULL vient donc d'encore plus tôt. Candidats observés dans le même run
(à instrumenter, PAS à stubber à l'aveugle) :
- Import#32315 `Q4qBuN-c0ZM` → -2143223530 (0x80430016) (ret=0x800F98375)
- Import#31749/31757 `rqwFKI4PAiM` (rdi=0xFFFFFFFF) → NOT_FOUND (ret=0x800085779)
- `6lNcCp+fxi4`, `Q4qBuN-c0ZM`, `rqwFKI4PAiM`, `Q3VBxCXhUHs` : absents de
  scripts/ps5_names.txt — à résoudre via le workflow capstone offline
  (scratchpad `yotei_dis.py`) en désassemblant les call-sites (ret-addresses
  ci-dessus) dans l'eboot déchiffré.

### Blocage tertiaire connu (inchangé)

`sceAgcCreateShader` type=5 (hull shader) : 6 échecs/run, non fatals — voir
section plus haut. Hypothèse prête (case 5 → lo=0x10A hi=0x10B).

## 2026-07-17 (suite) — Chaîne du NULL remontée et résolue

### Méthode (capstone offline + hash NID)

1. Désassemblage de la fonction 0x801612680 : writer de registres UC en
   anneau. Le NULL naît au `call 0x8016cced0` (thunk) qui retourne rax=0,
   stocké [rbx+0x30], puis propagé dans `6lNcCp+fxi4` (rdi=0) et dans un
   memcpy libc vers un buffer NULL.
2. Mapping GOT→NID reconstruit statiquement depuis les relocations JMPREL
   de l'eboot (strtab 0x1FB9990, symtab 0x1FBCCE8, jmprel 0x1FC0EE8) ;
   bibliothèques résolues via les tags 0x61000049 du PT_DYNAMIC.
3. Identités confirmées : hvUfkUIQcOE=sceAgcDcbSetUcRegistersIndirect (le
   NULL vient de LUI : TryAllocateCommandDwords → buffer plein),
   6lNcCp+fxi4=sceAgcSetUcRegIndirectPatchSetAddress, Q3VBxCXhUHs=memcpy
   (hash NID vérifié), Q4qBuN-c0ZM=sceNetSocket (échec toléré),
   rqwFKI4PAiM=sceKernelAprWaitCommandBuffer, xddD23+8TfQ=
   libSceNpEntitlementAccess (boucle de 9, tolérée),
   xSAR0LTcRKM=sceAgcDcbJump, h9z6+0hEydk=sceAgcSuspendPoint.
4. Cause racine : **VEGu4dixjUg = sceAgcDcbJumpGetSize** (match hash exact,
   méthode validée sur memcpy/sceNetSocket/sceAgcCreateShader). Non résolu,
   il retournait 0x80020002 → le jeu stockait 0x20008000 comme réserve de
   dwords par chunk → pool de 64 chunks épuisé dès l'init → callback de
   croissance refusait (index chunk > 0x3F) → SetUcRegistersIndirect
   rendait NULL → crash memcpy.

### Fix (commit 751d49e) — VÉRIFIÉ

`sceAgcDcbJumpGetSize` retourne 16 (= paquet IT_INDIRECT_BUFFER de 4 dwords
que notre DcbJump écrit). Résultat mesuré : progression de l'import ~#32K
à **#3 691 520** (~115×), les callbacks de croissance réussissent, le jeu
TERMINE son init renderer.

### NOUVEAU MUR : boucle de poll sceVideoOutGetFlipStatus

Le jeu atteint la logique de présentation et poll `sceVideoOutGetFlipStatus`
(SbU3dwp80lQ, handle=1) ~3,6 M de fois depuis l'import ~#100K ; c'est le
garde anti-boucle qui a tué le run (import#3691520, ret=0x800CD1335), pas
un crash du jeu. Même classe que les stalls flip Quake/Doom (cf. mémoires
quake-flip-pacing-stall / doom-flip-loop-root-cause / vblank-implicit-flip).
Particularité Yotei : **zéro sceAgcDriverSubmitDcb dans tout le run** — le
jeu poll le statut flip sans avoir soumis par ce NID ; soit il soumet par
un autre chemin, soit il attend un compteur vblank/flip que notre
GetFlipStatus ne fait pas avancer. À instrumenter : contenu du FlipStatus
retourné + champ que la boucle 0x800CD1335 teste (désassembler autour).

### Piste APR (probable ex-« compteur 6 » de FaWorkerIo1)

`sceKernelAprWaitCommandBuffer(-1, 3, n, n-1, out)` appelé avec id=-1
(« attendre tout ») → notre impl exige un id précis → NOT_FOUND, répété
pour n=1..7. Le jeu tolère (ça ne bloque plus le boot ici), mais c'est le
middleware I/O async du shader-cache (all_shaders.xpps). xnetcat a une impl
de ce NID (sharpemu-xnetcat/KernelAprCompatExports.cs) — à comparer.

### Boucle GetFlipStatus — RÉSOLUE (commit dcd5e8b)

Désassemblage de la boucle (ret 0x800CD1335, décrypté eboot) :

```
lea rsi, [rbp-0xb0]        ; buffer FlipStatus, JAMAIS pré-zéré côté guest
call SbU3dwp80lQ            ; sceVideoOutGetFlipStatus(handle, &status)
cmp dword ptr [rbp-0x7c], 0 ; teste offset 0x34 de la struct
je  <sortie>                 ; 0 → pas de flip en attente
mov edi, 1 ; call 1jfXLRVzisc ; sceKernelUsleep(1)
...        ; call SbU3dwp80lQ ; re-teste
jne  <boucle>
```

Notre `VideoOutGetFlipStatus` (VideoOutExports.cs) n'écrivait que les
offsets 0x00-0x27 (count, réservé×3, currentBuffer). L'offset 0x34, jamais
touché, contenait de la mémoire de pile non initialisée côté guest → si
non nulle par hasard, boucle infinie (3,6M appels avant que le garde
anti-boucle tue le process).

Fix : zéro-remplissage étendu jusqu'à 0x37 inclus. Justifié par un
invariant déjà présent dans le code, pas une invention : `SubmitFlip` est
synchrone (met à jour FlipCount/CurrentBuffer immédiatement,
VideoOutExports.cs:1090-1091) et l'export `sceVideoOutIsFlipPending`
retourne toujours "non" — donc "aucun flip en attente" partout est déjà
la sémantique du reste du code.

Vérifié : 0 appel `GetFlipStatus` dans le run suivant (contre 3,6M avant),
le jeu enchaîne sur un vrai travail GPU compute (`agc.compute_writer
size=960x540 op=ImageStore fmt=4`).

### NOUVEAU MUR : CLR fatal error pré-existant (documenté, STOP)

```
Fatal error.
Invalid Program: attempted to call a UnmanagedCallersOnly method from managed code.
   at DirectExecutionBackend.CallNativeEntry
   at DirectExecutionBackend.ExecuteGuestContinuationEntry
   at DirectExecutionBackend.ExecuteBlockedGuestThreadContinuation
   at DirectExecutionBackend.RunGuestThread
```

Exit 6 = fatal error du runtime .NET lui-même (pas un `Environment.Exit`
de notre code — grep du source ne trouve aucun `exit(6)`, cohérent avec
un fail-fast CLR). Ce crash est **déjà documenté** dans la mémoire
`unmanagedcallersonly-fatal` : CLR fatal sur les cycles de
création/sortie de thread guest, préexistant, sans lien avec le travail
d'aujourd'hui. Pas d'instrumentation supplémentaire faite dans cette
session — à reprendre séparément (probablement lié au cycle de threads
audio ou de continuation de thread guest bloqué, à confirmer par capture
du NID/nom de thread juste avant le fatal).

### CLR fatal UnmanagedCallersOnly — hypothèse structurelle testée et écartée

Tentative : reconnecter `RunGuestEntryStub`/`NativeGuestExecutor` (isolation
sur threads OS natifs, code déjà présent dans
`DirectExecutionBackend.NativeWorker.cs` avec commentaire décrivant EXACTEMENT
ce crash, mais **zéro appelant** dans toute la base — mécanisme orphelin,
jamais câblé). Câblé aux 3 sites d'appel direct de `CallNativeEntry`
(`ExecuteGuestThreadEntry`, `ExecuteGuestContinuationEntry`,
`ExecuteEntry`). Build OK, 160/160 tests Libs passent, Quake tourne 40s sans
régression (0 fatal). **Mais le crash Yotei persiste identique** (même point,
juste ~7000 imports plus tôt/tard selon le run — variance de timing, pas un
changement réel). Hypothèse invalidée par le run → **reverté** (règle 3).

Root cause réelle trouvée séparément (voir section suivante) : ce n'était
PAS un problème d'isolation de thread, mais un import non résolu classique —
la même famille de bug que `DcbJumpGetSize` et `FlipStatus`, pas une race
de threading.

### Import non résolu dbOlWdppb4o → AV lecture — RÉSOLU (à committer)

Après le fix `DcbJumpGetSize`, le run avançait jusqu'à ~import #25-32K puis
crashait de façon NON déterministe (parfois CLR fatal UnmanagedCallersOnly
exit 6, parfois AV native exit 139) — signature classique de comportement
indéfini déclenché par de la mémoire non initialisée, pas une vraie race.

Désassemblage (décrypté eboot) : le crash AV (read à 0x80A790D40, dans une
région réservée de 32 Go jamais committée) se produit dans une fonction
générique de sondage de table de hachage (probing quadratique avec offsets
-0xD4/-0x187) appelée depuis 0x8009F91FA avec `edx=0x20` (constante,
PAS lue d'un header). Elle lit 32 paires (offset,valeur) depuis le buffer
`[rbp-0x130]` et utilise le premier élément de chaque paire comme INDEX de
tableau — un index issu d'octets de pile guest non initialisés produit un
index absurde → lecture hors limites.

Ce même buffer `[rbp-0x130]` est le `ucRegistersAddress` de
`sceAgcCreatePrimState` (D9sr1xGUriE, DÉJÀ implémenté, écrit seulement les
3 premières paires = 24 octets), immédiatement suivi d'un appel à
**`dbOlWdppb4o`** avec le MÊME pointeur de base — confirmé non résolu dans
le log (`Import#31017 unresolved: nid=dbOlWdppb4o`). Symbole réel non trouvé
(absent de ps5_names.txt, brute-force hash infructueux sur ~230 candidats) ;
implémenté sous le nom provisoire `sceAgcAddPrimStateRegisters` avec
commentaire documentant la preuve.

Fix : zéro-remplissage de `[rdi+24, rdi+0x100)` (les 29 paires que
`CreatePrimState` ne remplit pas, dans la fenêtre exacte scannée par
l'appelant), préservant les 3 premières paires déjà écrites par
CreatePrimState. Justification : un index de sonde à 0 reste dans les limites
du tableau réel (contrairement à la garbage précédente) ; les valeurs à 0
échoueront simplement le test d'égalité du probe et seront ignorées comme
"registre absent" — dégradation silencieuse acceptable, pas un crash.

**Vérifié** : le run dépasse maintenant l'import **#226 511** (contre un
crash systématique vers #25-32K avant), toujours en cours au moment de la
rédaction, aucun fatal/AV. Prochain rapport après la fin du run en cours.

### CLR fatal UnmanagedCallersOnly — CAUSE RACINE TROUVÉE (cherry-pick 45759b5)

Relecture des derniers runs de la nuit (`log_yotei_longwatch1.txt`,
`log_yotei_wercapture1.txt`, POSTÉRIEURS au fix primstate) : le fatal
persistait, non déterministe vers l'import #31K, toujours sur la pile
`ExecuteBlockedGuestThreadContinuation → CallNativeEntry`. Ce n'était donc
PAS entièrement expliqué par dbOlWdppb4o.

Cause : la branche `fix/yotei-boot-deadlock` n'a jamais reçu le commit
**b87efce** (créé le 16 au soir sur `fix/quake-render-aliasing`,
vérifié par `git branch --contains`) qui restaure dans le pré-filtre VEH
natif (`WindowsFaultHandling.CreateHandlerThunk`) les deux protections
silencieusement perdues par le port d'abstraction hôte 5629beb :
1. le **check de plage RIP** (faute avec RIP ≥ 0x7FF0'00000000 = code
   JIT/système → CONTINUE_SEARCH, jamais le handler managé) ;
2. les 3 codes debug bénins (OutputDebugString ANSI/wide, SetThreadName).

Sans le check RIP, une faute levée pendant que le thread est en mode GC
coopératif entre dans le handler managé via le thunk reverse-P/Invoke →
FailFast CLR « attempted to call a UnmanagedCallersOnly method from
managed code » — la signature exacte observée. Mécanisme déjà documenté
et prouvé sur Quake (mémoires `unmanagedcallersonly-fatal`,
`quake-fault-trampoline-regression-fixed`).

Fix : cherry-pick de b87efce → **45759b5** (mêmes 3 fichiers : codes +
check RIP + test de régression direct du thunk émis). Build 0 erreur,
tests trampoline 2/2 OK.

Vérification : run `log_yotei_ripfix1.txt` — **#2 089 863 imports sans
aucun fatal, process encore vivant** (arrêté manuellement) ; les deux runs
précédents mouraient à ~#31K (×67 plus loin). État final du run : thread
principal en trylock EBUSY (bénin), 3 queues compute suspendues sur des
labels ==1 jamais écrits, zéro submit graphique, zéro flip.

### CORRECTIF au diagnostic UCO : le check RIP ne suffisait pas

Run suivant (`log_yotei_batch2.txt`) : fatal identique à #33K AVEC le check
RIP en place. Le run à 2M était de la chance (crash non déterministe ;
primstate1 avait atteint 1,8M sans le fix non plus). Le check RIP reste
correct (prouvé sur Quake) mais ne couvre pas la cause Yotei.

**Vraie cause + fix (de6903f)** : tous les stubs guest s'exécutaient inline
sur des threads CLR via `CallNativeEntry` — frames guest sans unwind info
CLR empilées au-dessus de frames managées ; toute fenêtre où la comptabilité
de mode de thread diverge de la pile réelle fail-fast le runtime (mécanisme
décrit noir sur blanc dans l'en-tête de DirectExecutionBackend.NativeWorker.cs).
Le pool `NativeGuestExecutor` (threads OS bruts, zéro frame managée sous le
guest) existait, complet, avec **zéro appelant**. Câblé aux 3 sites
(`RunGuestEntryStub`). La session précédente avait testé ce câblage et
conclu « inefficace » — mais AVANT le fix dbOlWdppb4o dont l'UB produisait
la même signature de fatal : verdict contaminé, retesté proprement.
Vérif : **3/3 runs franchissent la fenêtre #31-33K** (>160K imports, 0 fatal)
contre crash immédiat à #33K sans le câblage. 162/162 tests Libs OK.
Kill switch : `SHARPEMU_DISABLE_NATIVE_GUEST_WORKERS=1`.

### Batch de NIDs résolus par hash (dfdb338)

Les 14 imports non résolus du run passés au hash NID contre ps5_names.txt :
- **BIPexNBSGog = sceAgcDcbCondExec** → implémenté (paquet IT_COND_EXEC réel,
  0x22 ; le walker saute les ops inconnus → prédicat toujours-vrai, dégradation
  sûre).
- **tU5e3f9gSiU = sceKernelIsTrinityMode**, **BfBDZGbti7A =
  sceAgcGetIsTrinityMode** (détection PS5 Pro) → répondent « non ».
- create_shader type=5 (hull) : case 5 => 0x10A/0x10B ajouté.
- Restants tolérés : scePsmlMfsrInit/GetSharedResourcesInitRequirement
  (upscaler PSSR — 3 autres NIDs MFSR déjà stubés NOT_FOUND par f0c8603),
  sceNpRegisterNpReachabilityStateCallback, sceNpSessionSignalingCreateContext2,
  sceHttpSetConnectTimeOut, sceNetGetMacAddress, sceNpTrophy2GetTrophyInfoArray,
  sceGameLiveStreamingInitialize, sceKernelGetOpenPsId,
  sceNpEntitlementAccessGetSkuFlag/GetAddcontEntitlementInfo (le ×9).

### PERCÉE : sceKernelAprWaitCommandBuffer(-1) débloquait all_shaders (13f08c3)

`id=-1` (wait-all) retournait NOT_FOUND ; nos soumissions APR se complètent
de façon synchrone au submit → wait-all = succès immédiat. Résultat mesuré
(`log_yotei_aprfix1.txt`, SHARPEMU_LOG_AMPR=1) : le jeu **streame
`cache_ps5/all_shaders.xpps` par MILLIERS de soumissions APR** (header 0x58,
puis chunks ; >9 000 soumissions et ça continue), avec des waits par id
exact qui réussissent. C'est la machine à états `all_shaders` (bloquée à
2/6 depuis le 16) qui avance — l'ancien « compteur à 6 » de FaWorkerIo1.

### Post-APR : les waits compute se résolvent, puis DEADLOCK présenteur (cec6bee)

Run tracé SHARPEMU_LOG_AGC=1 (`log_yotei_agctrace1.txt`) : le DCB graphique
(submission 6) contient `release_mem dst=0x2000000020 data=1` → notre wait
registry résume `acb.compute[56]` (waited_ms=0,486) dont le payload ASCII
se nomme **« Clear G-Buffer »** — le rendu de frame commence réellement.
Les « 3 waits suspendus » des runs non tracés se résolvaient donc en
silence (les resumes ne se loguent qu'avec LOG_AGC).

La trace s'arrête net au premier DISPATCH compute (op=0x15). Dump du
process vivant (`yotei_dispatch_stall.dmp`, pstacks) :
- thread guest dans `DriverSubmitDcb` (tient gpuState.Gate) bloqué dans
  `ObserveComputeDispatch → VulkanVideoPresenter.WaitForGuestWork →
  Monitor.Wait` ;
- GPU-wait monitor + 2 callbacks `SubmitOrderedGpuSideEffect` en
  `Monitor.Enter` sur le même gate ;
- **aucun thread présenteur Vulkan vivant**.

Cause : `SubmitComputeDispatch` enfile du travail et retourne une séquence
même sans consommateur ; seuls les chemins DRAW démarraient le présenteur.
Yotei fait du compute avant tout draw/VideoOut → attente infinie sous le
gate → pipeline entier gelé (c'était le vrai mur « jamais de flip »).
Fix cec6bee : démarrer le présenteur depuis le chemin compute, comme les
chemins draw (Run() a déjà un fallback de taille de fenêtre).

### PREMIÈRE FRAME PRÉSENTÉE (aca2bf4) 🎉

Avec le présenteur démarré, le jeu a atteint l'enregistrement des display
buffers : `sceVideoOutRegisterBuffers2` (rKBUtgRrtbk, résolu par hash) et
`sceVideoOutUnregisterBuffers` (N5KDtkIjjJ4) échouaient en INVALID_VALUE —
notre impl rejetait `category != 0` ; Yotei passe **category=1** (3 sets de
2 buffers 3840x2160). Fix aca2bf4 : catégorie inconnue acceptée + tracée.

Résultat (`log_yotei_regbuf1.txt`) :
```
Vulkan VideoOut presented splash: 3840x2160
Vulkan VideoOut presented first frame: 3840x2160
```
**Première présentation de frame de l'histoire de ce titre sur SharpEmu.**
Fenêtre ouverte ("SharpEmu - Ghost of Yotei [PPSA26344] v01.008.000"),
contenu encore blanc (screenshot yotei_window2.png) — le contenu 4K du
display buffer n'est pas encore traduit/composé, et une seule frame
présentée pour l'instant ; le jeu continue de travailler derrière
(imports diversifiés, plus de régime figé).

### État fin de session (run `log_yotei_regbuf1.txt` laissé vivant)

Le run dépasse **#7 000 000 d'imports** (ancien record : 3,69M tué par le
garde anti-boucle ; celui-ci vit car l'activité est diversifiée). 33 threads
guest nommés (MovieDecoder, FaWorkerIo1, JobWorker×20, Scream, snd_stream…),
2 présentations (splash + first frame 3840x2160), fenêtre blanche stable.
Nota : l'écran logo Sucker Punch est à fond blanc — le « blanc » est
peut-être un vrai début de contenu (logo pas encore dessiné dessus).

### Hypothèses en attente (dans l'ordre)

1. Frame 2 : une seule présentation puis plus rien — même classe que les
   stalls « frame 2 » Doom/Quake. Vérifier ce que le jeu attend entre la
   frame 1 et la frame 2 (event flip/vblank sur equeue ? MovieDecoder ?).
   Piste : tracer les WaitEqueue + events VideoOut sur un run dédié.
2. Contenu blanc : traduction du display buffer 4K category=1
   (compression ?) — le rendu écrit-il vraiment dans les buffers
   enregistrés ?
3. `xddD23+8TfQ` = sceNpEntitlementAccessGetAddcontEntitlementInfo
   (résolu par hash, PAS encore implémenté) : le jeu re-boucle dessus
   périodiquement (probing DLC, args : rdi=serviceLabel=0, rsi=label ptr,
   rdx=info* out, r8=0x40). L'implémenter dans
   NpEntitlementAccessExports.cs à côté de GetAddcontEntitlementInfoList —
   trouver le bon code d'erreur « non possédé » avant (ne pas improviser).
4. ~~Trylock EBUSY permanent~~ **ÉLIMINÉ** (run `log_yotei_pthreads1.txt`,
   SHARPEMU_LOG_PTHREADS=1) : le spinner est **ScreamWorker1** (mixeur
   audio, host_priority=Highest) qui polle en trylock-or-skip les buffers
   gardés par `snd_stream_parsing_thread`/`snd_stream_reader_thread` —
   motif normal de mixeur temps réel, propriétaires vivants qui alternent,
   pas un deadlock, pas le gate frame-2. Anomalie mineure relevée au
   passage : 5 trylocks EBUSY avec owner=0/recursion=0 (la file FIFO
   stricte refuse un mutex LIBRE parce que réservé par un waiter) — un
   vrai scePthreadMutexTrylock réussirait ; à corriger si un titre s'y
   coince un jour.

Note : le run record (133M imports) s'est terminé proprement par la
fermeture de la fenêtre (`videoout-window-closed`, requested=False —
clic extérieur), pas par un crash.

## 2026-07-17 (3e session) — Gate frame-2 : interruptions EOP manquantes

### Localisation du gate (preuves)

Run tracé `log_yotei_frame2_agc1.txt` (SHARPEMU_LOG_AGC=1) puis
`log_yotei_equeue1.txt` (+SHARPEMU_LOG_EQUEUE=1) :

- La « first frame » présentée n'était que la frame noire de
  `HideSplashScreen` — le jeu n'a JAMAIS soumis de flip (0 submit_flip,
  0 « presented guest frame »).
- La frame 1 réelle (21 draws `dcb_draw_index_auto`, dispatches compute
  « Clear G-Buffer », writeback 8,8 Mo) s'exécute, puis 3 queues ACB
  restent suspendues à jamais : compute[48] sub3 sur 0x2011831650,
  compute[72] sub5 sur 0x2011669FE0, compute[56] sub4 sur 0x2011882330
  (cmp==1, producer=none-observed). Aucun paquet d'aucune queue n'écrit
  ces labels : ce sont des kicks écrits par le CPU.
- Causalité observée : ligne 3086 `equeue.wait-deliver` (equeue 4) →
  ligne 3088 le label 0x2000000480 est écrit et compute[83] reprend.
  C'est le thread d'interruption du driver AGC (boucle guest
  0x801116500, WaitEqueue ret=0x801116559) qui écrit les kicks.
- Désassemblage de la boucle : au réveil il compare un compteur attendu
  `[rbx+0x158]` à `[table + idx*0x20]` (64 bits) où table =
  `[0x8044B9268]` = **0x2000000000** (lu dans le full dump au stall) —
  le pool de labels GPU. Si atteint → `0x800d932c0` = décrément d'un
  compteur de dépendances de job ; à zéro → sema.signal CJobManager
  (ret=0x800D93368) → le job écrit les kicks. Il se rebloque ensuite
  sur WaitEqueue : **sans réveil, pas de kick**.
- Le jeu n'enregistre qu'UN event kernel graphics : eq=4 id=0x52
  (sceAgcDriverAddEqEvent). Nos event_write le pingent (queues=1) mais
  les release_mem à bit interrupt (control>>24 != 0, forme nop ET forme
  standard int_sel) n'émettaient AUCUN event — sur hardware chaque
  release retiré à int_sel≠0 lève une interruption EOP. Le thread ne
  recevait qu'une fraction des réveils attendus → compteur de
  dépendances jamais à zéro → kicks jamais écrits → frame graph gelé →
  jamais de flip.
- A/B intermédiaire : SHARPEMU_AGC_SUBMIT_COMPLETION_EVENT=1 (event de
  complétion de la seule submission graphics) réveille le thread une
  fois de plus mais ne suffit pas (`log_yotei_submitevt1.txt`).

### Fix en test

`ApplySubmittedReleaseMem` + `ApplySubmittedStandardReleaseMem` :
lire le champ interrupt (nop-form : (control>>24)&0xFF ; standard :
int_sel=(control>>24)&7) et, dans la même action ordonnée que
l'écriture du label, déclencher
`TriggerRegisteredEventsByFilter(graphics, data)`. Run de vérification
en cours (`log_yotei_eopint1.txt`).

## 2026-07-17 (4e session, mode autonome) — PREMIER VRAI FLIP, chasse au gate frame-2

### Percée ring/chunk (commits 62d543d + 1cda5ed)

Le travail non commité de la 3e session (EOP no-coalesce equeue + chunk
chaining du ring + park sur mot de ring non écrit) donne, run
`log_yotei_chunkadv1.txt` :

```
vk.flip_capture ... submission=6 addr=0x5005160000 3840x2160
Vulkan VideoOut presented guest frame: image=0x5005160000 3840x2160
```

**Premier flip soumis par le jeu lui-même** (pas la frame noire de
HideSplashScreen). Les deux `chunk_advance` + le `ring_tail_pending`
fonctionnent. Committé en 2 commits par concern.

Le run est ensuite mort d'un exit 6 (UCO fatal connu, non déterministe,
~7,1M imports) — analyse : AUCUN paquet PM4 inconnu ni NID manquant en
cause ; après le flip, plus aucune submission graphique (sub6 unique),
7M imports de spin audio bénin (ScreamWorker1).

### Ordre EOP : event_write différé à la fin de la passe (commit en cours)

Analyse de la séquence finale de sub6 : `event_write type=0x10` (dw=58)
réveillait le thread d'interruption AGC **au parsing**, AVANT que le
`write_data dst=0x2000000000` (dw=64, le pool de compteurs) ne soit
appliqué (ordered seq 133 < 137). Le thread relisait ses compteurs pas
encore à jour et se rebloquait → lost wakeup → plus aucun réveil ensuite
(dernier wait-block ligne 6732, aucun deliver après). Sur hardware l'EOP
part après le drain du pipe, donc après le write_data.

Fix : les triggers d'event_write sont collectés pendant la passe de
parsing (`SubmittedDcbState.PendingEventTriggers`) et flushés comme
actions ordonnées à la sortie de la passe (fin/suspension/park/jump) —
ils séquencent alors après tous les write_data du même window. Vérifié
dans `log_yotei_eopdefer3.txt` : write_data seq 131/132 appliqué AVANT
les events 0x07/0x07/0x10 (lignes 6647→6664). Le flip #1 passe toujours.

### Diagnostic live du gate frame-2 (dump mémoire + RIP des threads)

Process vivant gelé post-flip, inspection par ReadProcessMemory +
GetThreadContext (scripts scratchpad readmem.ps1 / threadrips.ps1 /
readfence.ps1) :

- **Thread principal (tid 24268) : PAS bloqué sur un import — il spin en
  pur code guest** à 0x800CD5002 (boucle rdtsc), invisible des logs.
  Désassemblage : il attend `table[idx]≥expected` sur la table de labels
  `[0x8044B9268]` = 0x2000000000 (stride 0x20). Valeurs lues live :
  **wait1 : table[2] (0x2000000040) ≥ 1 ; wait2 : table[3]
  (0x2000000060) ≥ 1 — les deux entrées encore à 0.**
- Table live : table[0]=2 (écrit par le write_data), table[1]=1 (écrit
  par release_mem dst=0x2000000020), table[2..3]=0.
- Les 3 kicks attendus par les computes suspendues (0x2011831650,
  0x2011882330, 0x2011669FE0) : tous à 0.
- Sema CJobManager (handle 3) : 10 waiters, count=0, **aucun signal de
  tout le run** — le thread d'interruption s'est réveillé 3× après le
  write_data mais sa condition de fence n'est jamais passée.

Chaîne du blocage : main thread attend table[2]/[3] (complétion des
queues compute) ← computes suspendues sur kicks CPU ← kicks écrits par
les JobWorkers ← sema CJobManager jamais signalé ← condition du thread
d'interruption jamais satisfaite.

### Expérience en cours : re-ping périodique level-triggered

Le thread d'interruption rescanne sa liste de fences à chaque réveil
(payload kevent ignoré) → un ping périodique est sûr par construction.
Ajout dans `MonitorGpuWaits` (tourne déjà tant que des waiters GPU
existent, 1-16 ms) : si aucun progrès,
`TriggerRegisteredEventsByFilter(graphics, 0)` (~60 Hz en régime
stable). Run `log_yotei_eopping1.txt` en cours. Si le sema ne part
toujours pas, la condition du thread d'interruption exige table[2]/[3]
elle-même → le modèle « kicks par jobs CPU » est faux et il faudra
désassembler la liste de fences réelle du thread d'interruption
([rbx+0x158]).

Note UCO : le fatal exit 6 a aussi frappé 2× de façon précoce
(#13K, à register_buffers2) sur des recompiles — toujours
non déterministe, la fenêtre se déplace avec le layout JIT. Non traité
dans cette session (mission = boucle de rendu).

### Ping périodique INVALIDÉ par mesure live — la comptabilité est par comptage

Lecture des champs du thread d'interruption dans le process vivant
(scan de sa stack pour l'objet driver V=0x806981F00, stable d'un run à
l'autre ; scripts scanirq.ps1/readirq.ps1) :

- Fence courante : `[V+0x158]=1` (expected), `[V+0x15C]=15` (idx) →
  table[15]=1 : SATISFAITE. Le thread a traité tout ce qu'on lui a
  soumis.
- Objet de complétion V+0x2C40 : `lock dec [obj+4]` à CHAQUE kevent
  reçu, signal des continuations UNIQUEMENT au passage exact par 0
  depuis l'état armé (state==1). Avec le ping 60 Hz : depcount →
  **-6739**. Sans ping : **-11** → nous SUR-délivrons déjà 11 kevents
  (les event_write 0x46, qui sur hardware n'interrompent JAMAIS — pas
  de champ int_sel dans IT_EVENT_WRITE).
- Conséquence : les continuations du driver (qui écrivent les kicks du
  frame graph, ex. 0x2000000480=1 observé le 16) sont affamées →
  pipeline gelé. Le ping est REVERTÉ.

### Le frame graph complet, prouvé par pokes mémoire live

Expérience décisive (WriteProcessMemory dans le process gelé) :

1. Écrire 1 dans les 3 kicks (0x2011831650, 0x2011882330,
   0x2011669FE0) → **les 3 computes suspendues REPRENNENT** (waited_ms
   ~527000), exécutent leur segment suivant (« Async Sky LUT Update »…),
   puis se suspendent sur les fences SUIVANTES (0x20118A4EE0,
   table[0x18], table[0x16]) — pipeline multi-étages.
2. Écrire table[2]=1, table[3]=1 (0x2000000040/60) → le main thread
   sort de son spin rdtsc (RIP 0x800CD5002→0x800CD51A2), 3e fence
   table[21] (0x20000002A0) lue par disasm et satisfaite à la main →
   **le jeu SOUMET la frame 2** : `agc.driver_submit_acb` × plusieurs,
   **submissions 7 et 8**, nouveaux dispatches Vulkan réels.

Chaîne causale complète du gate frame-2 :
main thread spin(table[2],table[3],table[21]) ← écrites par les tails
des queues compute ← suspendues sur des kicks ← écrits par les
continuations du thread d'interruption ← affamées par la comptabilité
kevent faussée (sur-livraison event_write + champ int des release_mem
agc-nop mal décodé, tous à 0 alors que le driver attend le réveil).

Diagnostic annexe : le padding zéro du ring N'est PAS traversable (îlots
de DONNÉES à 0x2011638F70 dans le chunk 2 — un skip a fait avaler des
données au parseur et avorté la soumission en silence). Le skip est
reverté ; le park sur mot nul reste la bonne sémantique.

### Fix en cours de test : appariement kevent ↔ release_mem

- `event_write` (IT_EVENT_WRITE 0x46) ne délivre PLUS de kevent
  (fidèle hardware).
- `release_mem` (formes agc-nop et standard) délivre UN kevent
  inconditionnellement, dans la même action ordonnée que l'écriture du
  label (le re-check de fence du thread voit le store).
- Boucle de convergence : au stall, lire depcount live —
  positif = sous-livraison, négatif = sur-livraison, ajuster les
  sources. Run `log_yotei_relmint1.txt` en cours.

### Fix affiné (non commité) : gating par pool d'adresse, pas par int_sel — INVALIDÉ par mesure fraîche

Le code non commité (`AgcExports.cs`) avait déjà remplacé la livraison
inconditionnelle ci-dessus par un gating `IsCpuVisibleLabel(dst)` :
seuls les `release_mem` dont la destination tombe dans
`[0x2000000000, 0x2000010000)` délivrent un kevent
`TriggerRegisteredEventsByFilter` ; les autres (adresses ring internes)
n'en délivrent aucun, indépendamment du champ `int_sel`. `event_write`
ne délivre plus jamais de kevent (`queues=none`).

Build fraîche (`dotnet build src/SharpEmu.CLI`, DLL horodatée après la
dernière édition source — pas de binaire périmé) + run neuf
`log_yotei_intselfix1.txt` (SHARPEMU_LOG_AGC=1 SHARPEMU_LOG_EQUEUE=1,
180s) :

- Résultat IDENTIQUE à toutes les tentatives précédentes : submission=6
  (flip #1) se termine, présente la frame, puis silence AGC total.
  Aucune submission=7/8, aucune activité agc.*/vk.* après la ligne du
  flip.
- Les 3 mêmes queues compute restent suspendues sur les mêmes kicks
  jamais écrits : compute[48]→0x2011831650, compute[56]→0x2011882330,
  compute[72]→0x2011669FE0 (tous `producer=none-observed`).
- **Mesure clé : sur les 12 `release_mem` tracés dans ce run (formes
  agc-nop ET standard confondues), `int_sel` vaut 0 dans 100% des cas**
  — y compris pour les `release_mem` vers le pool CPU-visible
  (0x2000000020, 0x20000001A0, 0x2000000400) qui délivrent bien un
  kevent (`woken=1`) grâce au gating par adresse, alors que leur
  `int_sel` est nul.

Conclusion : l'hypothèse « `int_sel` est le discriminant CPU vs GPU »
est FAUSSE pour ce titre — le jeu n'utilise jamais ce champ (toujours
0), donc un gating basé sur `int_sel != 0` livrerait ZÉRO kevent (pire
que le gating actuel par adresse). Le gating par pool d'adresse est le
seul des deux qui produit un signal, mais il ne suffit toujours pas :
les 3 kicks qui bloquent réellement les computes ne sont jamais écrits
par quoi que ce soit d'observé (aucun HLE, aucun paquet PM4, aucun
kevent délivré ne les touche). Le modèle « thread d'interruption AGC
réveillé par kevent → decrémente un depcount → sema CJobManager →
JobWorker écrit les kicks » reste une hypothèse non prouvée par le
tracing actuel — seul le poke mémoire live (session précédente) a
confirmé que ces 3 adresses, une fois écrites, débloquent tout. Ce qui
manque est le VRAI producteur de ces écritures, pas un problème de
livraison de kevent. Prochaine piste : tracer quel thread CPU
(JobWorker natif ou HLE) touche ces 3 adresses au niveau mémoire
pendant un run vivant, plutôt que continuer à ajuster la livraison de
kevent côté release_mem/event_write.

## 2026-07-17 (5e session) — Traque du flux de soumission AGC par désassemblage live

Session dédiée à retrouver, dans le code du jeu, qui est censé soumettre
les 3 buffers de préambule jamais soumis. Outillage : un mini-projet
console (`Iced` en NuGet, scratchpad, hors solution — `Libs`→`Core`
créerait un cycle) pour désassembler des dumps d'octets bruts capturés
en live via des sondes temporaires dans `AgcExports.cs` (`ctx.TryReadByte`
+ marche de chaîne RBP). Toutes ces sondes sont non committées, à
retirer avant tout commit final.

### Fausse piste éliminée : hypothèse « CPU bloqué ailleurs »

Snapshot live de tous les threads OS (`Process.GetCurrentProcess().Threads`
+ `IHostThreading.TryCaptureThreadRegisters`, déjà exposé proprement par
l'API — aucune tromperie nécessaire) 4s après le flip : sur ~85 threads,
un seul exécute du code invité, RIP=`0x800CD5002` — **exactement** la
boucle de spin `rdtsc` déjà identifiée en session précédente (avant le
chunk-chaining), qui poll `table[2]`/`table[3]` (0x2000000040/60). Tous
les autres threads dorment dans des attentes syscall host. Conclusion :
le thread principal n'est bloqué nulle part ailleurs — c'est bien le
même stall compute déjà connu, vu du côté CPU. Hypothèse « flush différé
au frame suivant » écartée : il n'y a pas de « suite » à atteindre, le
thread poll déjà le résultat du travail déjà soumis.

### La fonction qui construit les 3 buffers-préambule NE les soumet PAS

Remontée de pile depuis `sceAgcCbReleaseMem` (les 3 appels visent
`0x2011669FE0`/`0x2011831650`/`0x2011882330`, tous `ret=0x801612AA4`,
`guest=0x0` = thread principal) : ce leaf ne fait que construire le
paquet et retourner son adresse (`r13`, avec cache paresseux si déjà
non-nul) à son appelant, `0x8012F2B94`. Désassemblage complet de cette
fonction (~0x450 octets, capturé en un seul dump de 6000 octets) :
boucle sur plusieurs jobs, construit des paquets `CbDispatch`/
`CbReleaseMem`/`CbSetShRegisters` dans les 2 buffers globaux
`0x806AB2030` et `0x806AB69D0`, vérifie son canari de pile, et fait un
**`ret` propre** — aucun appel de soumission sur ce chemin. Le
« thunk PLT » `0x8016CCE00` suspecté un temps d'être la soumission est
en fait juste un autre constructeur `Cb*` appelé via indirection ABI
(NID résolu en live via lecture du GOT + hash à l'offset+8 du slot —
`nid_hash=0xE0FF0000`, absent des 154457 noms de `ps5_names.txt`, donc
non identifiable par nom ; l'algo de hash/nid a été vérifié correct par
un test de non-régression sur `sceAgcCbReleaseMem`→`wr23dPKyWc0`).

### Correction majeure : il n'y a que 3 buffers réellement orphelins, pas 6

En comparant les en-têtes (base/limite/curseur d'écriture, 4 qwords) des
6 adresses candidates aux `addr=` réellement soumis par
`sceAgcDriverSubmitAcb` : **`0x8044CEC08`, `0x8044CF258` et `0x806AB69D0`
sont en fait les MÊMES régions ring que les soumissions owner=48/56/72**
(leur premier qword == l'`addr=` exact de la soumission observée). Le
buffer non-committé `0x8012F2B94` construit `0x806AB69D0` comme la SUITE
du ring de compute[72] déjà soumis — pas un préambule séparé. Le vrai
jeu de 3 buffers jamais soumis (base ne correspondant à AUCUN `addr=`
connu) reste : `0x806AB2030` (écrit 0x2011669FE0, requis par le TOUT
PREMIER paquet de compute[72]), `0x804F70710` (écrit 0x2011831650,
requis par compute[48]) et `0x804F7AF50` (écrit 0x2011882330, requis par
compute[56]). Chaque queue réelle démarre littéralement par un
`WAIT_REG_MEM` sur le fence que SON buffer-préambule doit écrire —
d'où le blocage dès le premier paquet, jamais levé.

### La fonction-porte de soumission existe et fonctionne — pour les 3 bonnes queues

Remontée de la chaîne d'appel de `sceAgcDriverSubmitAcb(owner=48)` :
`immediate_ret=0x800AB4C4E` → `level0 ret=0x800CD4E3F` (à ~0x1C3 octets
du spin `rdtsc` — même fonction de boucle principale). Désassemblage de
`0x800CD4C00`-`0x800CD5400` : un bloc `0x800CD4E2A`-`0x800CD4E57` gardé
par `cmp byte [rbp-18E0h],0 ; jne <skip>` appelle une fonction
`0x800AB4A30` trois fois, une fois par queue (`lea rdi,[rel 8044CEC08h]`
puis `8044CF258h` puis `806AB69D0h`) — confirmé : c'est la porte
« soumettre si prêt » pour les 3 queues QUI MARCHENT, pas pour les
préambules. Le flag `[rbp-18E0h]` était à `0` (donc le bloc s'exécute),
cohérent avec les 3 soumissions observées.

### Piste ouverte (non conclue) : `0x806AB2030` réapparaît dans CETTE MÊME fonction

Recherche de `0x806AB2030`/`0x804F70710`/`0x804F7AF50` dans une fenêtre
élargie (`0x800CD3800`-`0x800CD4800`, 4096 octets) de la fonction de
boucle principale : **`0x806AB2030` apparaît à `0x800CD4390`**, juste
après une écriture CPU directe dans la table de labels GPU
(`mov rdx,[rel 8044B9268h]` = base 0x2000000000 ; `shl rcx,5` = stride
0x20 ; `mov [rdx+rcx],rax` = écriture de `table[idx]` depuis le CPU,
sans passer par un paquet PM4). `rdi=0x806AB2030` est chargé juste avant
un `call 0x8009F5E50` — donc probablement passé en argument à cet appel.
Motif répété : plusieurs paires (index, valeur) lues depuis des
adresses différentes (`0x8049059F0h` et voisines) sont écrites dans la
table puis un `call 0x8009F5E50` clôt chaque lot — ressemble à un
mécanisme générique de « vidage d'une file de complétions en attente »
(peut-être alimentée par les threads JobWorker), pas spécifiquement à
« soumettre le buffer 0x806AB2030 ». **Non confirmé** : est-ce que
`0x806AB2030` y est un vrai argument utile à `0x8009F5E50`, ou juste une
valeur de registre réutilisée d'un usage antérieur sans rapport (le même
global sert peut-être à plusieurs fins) ? `0x804F70710`/`0x804F7AF50`
n'apparaissent PAS dans cette fenêtre — donc si ce mécanisme est le bon,
il doit exister un site analogue ailleurs pour ces deux-là.

### Piste `0x8009F5E50` INVALIDÉE — c'est un compteur de profiling, pas une soumission

Désassemblage direct de `0x8009F5E50` (au lieu de deviner via son site
d'appel) : boucle sur un tableau de 256 (`0x100`) entrées avec
accumulation glissante (`bextr`, add/shift caractéristiques d'un
histogramme ou moyenne mobile), suivie d'une fonction de hachage à
mélange type SipHash (`rorx` par 5/15 bits + xor/add répétés, constante
magique `0xF1EA5EED`). L'argument réel traité est `esi` (un entier), pas
`rdi` — `rdi=0x806AB2030` chargé juste avant l'appel est une réutilisation
de registre non liée, pas un argument. **Conclusion : `0x8009F5E50` est
un compteur/télémétrie interne, sans rapport avec la soumission GPU.**
Cette piste est refermée.

### Bilan de la session : les 2 fonctions les plus prometteuses ne soumettent rien

À ce stade, la fonction qui construit les 3 buffers-préambule
(`0x8012F2B94`, confirmée `ret` propre sans soumission) ET la fonction de
boucle principale qui soumet réellement les 3 queues qui marchent
(`0x800CD3800`-`0x800CD5400`, porte de soumission `0x800AB4A30`
identifiée et fonctionnelle) ont toutes deux été désassemblées en
intégralité sans trouver le moindre code qui référence ou soumettrait
`0x806AB2030`/`0x804F70710`/`0x804F7AF50`. La piste « chercher le point
d'appel manquant dans le code CPU » est donc en grande partie épuisée
pour ces deux fonctions précises.

### Hypothèse « écriture par shader UAV » testée et ÉCARTÉE

Vérifiée via la trace existante `agc.compute_shader` (activée par
`SHARPEMU_LOG_AGC=1`, qui implique `_traceAgcShader`), qui journalise déjà
`global_buffers=[adresse:taille,...]` — l'adresse GPU de CHAQUE binding
mémoire globale/UAV, pour chaque shader compute distinct dispatché
(dédupliqué par adresse de shader). Sur le run complet (jusqu'au stall),
**9 shaders compute distincts** ont été dispatchés avant le blocage.
Leurs cibles `global_buffers` :
- 5× `0x201170xxxx` taille exactement `262144` (0x40000) — c'est le
  buffer-pont de scalaires AUTO-GÉNÉRÉ par SharpEmu lui-même pour
  chaque shader (taille fixe identique à chaque fois, non liée aux
  besoins réels du shader — signature claire d'un buffer interne, pas
  d'un UAV authored par le jeu).
- 2× vraies écritures UAV (`global_writes=True`, opcode
  `BufferStoreFormatXyzw`/`BufferStoreFormatXy`) : `0x5050BC0000` et
  `0x5007140000`/`0x5007168000` — toutes dans l'espace d'adresses VRAM
  `0x50...`, aucun rapport avec nos 3 cibles `0x2011...`.

**Aucun des 9 shaders ne référence `0x2011669FE0`/`0x2011831650`/
`0x2011882330`, ni leur voisinage.** L'hypothèse shader est donc écartée
pour tout ce qui a réellement été dispatché dans ce run. Réserve logique
(non testable directement) : un shader qui écrirait ces fences pourrait
être plus loin dans le flux de compute[48]/[56]/[72] eux-mêmes — mais
ces 3 files sont bloquées dès leur TOUT PREMIER paquet (`WAIT_REG_MEM`
à l'offset 0 de leur ring), donc un tel shader ne serait de toute façon
jamais atteint — et une queue ne peut de toute façon pas se débloquer
elle-même. L'hypothèse CPU (un `release_mem` porté par un buffer jamais
soumis) reste donc la plus probable ; la piste « écriture par shader »
est refermée.

Effet de bord noté au passage (sans rapport direct avec ce stall) :
4 des 9 shaders échouent à la traduction (`gpu=False` ou bail précoce) :
opcodes non supportés `DsSwizzleB32` (LDS), `Vop3Raw345`, `unknown-mimg
op=0x1F`, `unknown-sop1 op=0x14`. Gaps réels du traducteur de shaders,
mais qui ne ciblent pas nos 3 adresses — donc hors-sujet pour CE
blocage précis, à traiter séparément si besoin.

## 2026-07-17 (6e session) — Hypothèse kevent/depcount testée et INVALIDÉE

Mission dédiée à tester l'hypothèse « le thread d'interruption AGC (equeue
handle=4, `ret=0x801116559`, boucle `0x801116500`) est victime d'une
sur-livraison de kevent qui pousse son depcount interne (`V+0x2C40+4`)
sous zéro, l'empêchant de re-signaler CJobManager, donc les JobWorkers ne
tournent jamais, donc les 3 kicks ne sont jamais écrits ».

### Phase 1 : moniteur d'écriture mémoire sur les 3 adresses (`SHARPEMU_TRAP_KICKS=1`)

Un vrai piège matériel (DR7/page-guard) a été délibérément évité : une
session antérieure sur cette même codebase a documenté un fail-fast
Windows (`0xC0000409`) contournant le gestionnaire d'exception vectorisé
lors du single-step d'un thread invité en exécution native (voir mémoire
`yotei-watchpoint-failfast-not-tf`) — risque de crash sans bénéfice net
pour un simple diagnostic. À la place : un moniteur en arrière-plan
(`AgcExports.EnsureKickMonitor`/`RunKickMonitor`, démarré une fois depuis
`DriverSubmitAcb`, réutilise le motif déjà existant de `MonitorGpuWaits`
qui capture un `CpuContext` pour lecture depuis un thread de fond) sonde
les 3 adresses toutes les ~1 ms ; à la première transition hors de zéro,
il capture RIP/RSP/RBP de **tous** les threads OS via
`IHostThreading.TryCaptureThreadRegisters` (primitive suspend/read/resume
déjà exposée proprement, utilisée sans New code Win32) et remonte la
chaîne RBP (5 niveaux) du thread trouvé en code invité.

**Résultat, 2 runs de 45-60s (avec `SHARPEMU_LOG_AGC=1
SHARPEMU_LOG_EQUEUE=1`, puis à nouveau avec `SHARPEMU_LOG_GUEST_THREADS=1`)
: zéro écriture détectée sur les 3 adresses, du début à la fin, bien
après que les 3 queues aient atteint leur `WAIT_REG_MEM` bloquant.** Le
moniteur est confirmé armé (`agc.kick_monitor_armed`) et les runs
atteignent bien le flip (frame présentée) et le stall (les 3
`wait_suspended` apparaissent). Confirme, par une méthode indépendante de
tout le tracing PM4/HLE déjà fait, qu'aucun store CPU brut (sans
empreinte HLE, ex. un JobWorker qui ferait `mov [addr],1` directement)
n'a lieu non plus.

### Phase 2 : le mécanisme kevent → wake → resume est vérifié SAIN

Repéré une fausse anomalie puis élucidée : dans ce run, l'equeue handle=4
appelle `equeue.wait-block` 6 fois mais ne montre jamais
`equeue.wait-deliver`. En lisant le code (`KernelEventQueueCompatExports.
KernelWaitEqueue`/`ResumeWaitEqueue`), la trace `wait-deliver` ne
s'émet QUE sur le chemin de livraison synchrone immédiate (dans
`KernelWaitEqueue` lui-même, quand `deliveredCount>0` au moment de
l'appel) ; le chemin de reprise après blocage réel
(`EqueueWaiter.Resume()` → `ResumeWaitEqueue`) ne trace RIEN — absence de
log, pas absence de réveil. Vérifié en activant `SHARPEMU_LOG_GUEST_
THREADS=1` : `DirectExecutionBackend.WakeBlockedThreads` journalise déjà
`guest_threads.wake key=sceKernelWaitEqueue:...004 count=1`, et ce
message apparaît **exactement 5 fois**, à chaque fois immédiatement après
un `release_mem` ayant réellement déclenché `woken=1` — correspondance
parfaite. `EqueueWaiter.TryWake()` (`=> HasPendingEvents(handle)`) a donc
retourné `true` les 5 fois, prouvant que la file d'attente cooperative
(`_guestThreads` dans `DirectExecutionBackend`) a bien trouvé et réveillé
le thread bloqué, sans aucun échec. **Le duo réveil/reprise fonctionne
correctement pour ce equeue — aucune trace d'un depcount qui empêcherait
un signal ultérieur.**

### Conclusion : hypothèse kevent INVALIDÉE → Phase 4 (pas de fix Phase 3)

Les deux piliers de l'hypothèse du plan de mission sont directement
contredits par la mesure :
- Aucune écriture, jamais, sur les 3 adresses de kick (Phase 1) — le
  problème n'est pas « le CPU écrit mais avec du retard/perte », c'est
  qu'aucun code, jamais, dans ce run, n'écrit ces adresses.
- Le mécanisme de réveil du thread d'interruption AGC fonctionne
  correctement pour les 5 complétions réelles qu'il reçoit (Phase 2) —
  rien n'indique qu'un depcount cassé bloque un signal qui, sinon,
  aurait dû partir.

Conformément à la branche Phase 4 du plan de mission : le vrai problème
n'est pas un réveil manqué, c'est l'absence totale de consommateur/
appelant de soumission pour les 3 buffers-préambule
(`0x806AB2030`/`0x804F70710`/`0x804F7AF50`) — exactement la conclusion
déjà posée en session 5 après désassemblage complet des deux fonctions
candidates. **Aucun fix Phase 3 n'a été implémenté** : ajouter une garde
« ignorer les kevent supplémentaires si depcount≤0 » n'aurait aucune
justification dans le code observé (rien n'indique un depcount négatif
ni une sur-livraison réelle pour CE mécanisme) et violerait la consigne
« pas de stub aveugle » — un fix sans preuve de la panne qu'il est censé
corriger n'est pas un fix, c'est un pari.

**Livrable réel de cette session** : le moniteur Phase 1
(`SHARPEMU_TRAP_KICKS=1`, gardé dans l'arbre — code sûr, opt-in,
réutilisable pour tout futur stall de ce type) plutôt qu'un correctif de
la cause racine, qui reste non localisée.

## 2026-07-17 (7e session) — Chaîne d'appel du système de jobs entièrement remontée (5 niveaux)

Suite directe : désassemblage du trampoline générique des threads worker
et de ses appels imbriqués, jusqu'au vrai site de dispatch par job.
Sondes ajoutées au même point que le moniteur Phase 1 (`SHARPEMU_TRAP_
KICKS=1`), lisant la mémoire vive plutôt que par calcul statique — les
adresses `arg=`/`userdata=` viennent des logs « Scheduled guest thread »
déjà vus, confirmées stables entre runs.

Chaîne résolue (chaque niveau confirmé par lecture mémoire live, pas par
supposition) :
1. `entry=0x8001E1AE0` (partagé par TOUS les threads : JobWorker1-10,
   JobWorkerLow1-10, Uds, Trophy, ProxySetSync...) — trampoline
   générique : `arg={champ0, entry_fn@+8, userdata@+0x10}`, appelle
   `[arg+8](userdata)`. Confirme qu'aucune spécialisation par type de
   thread n'existe à ce niveau — il faut lire la donnée par thread.
2. Pour JobWorker1/2/JobWorkerLow1 **et** Uds, `arg+8` résout tous vers
   `0x8001E2300` — encore générique : `push rbp; ... call qword
   [rdi+0x58]` avec `rdi = userdata`, un slot vtable-style sur l'objet
   userdata lui-même (motif C++ polymorphe, pas un vrai job-dispatch).
3. `[userdata+0x58]` DIFFÉRENCIE enfin par classe de thread :
   `JobWorker1/JobWorker2/JobWorkerLow1` → `0x800D924F0` (identique pour
   les 3, confirmant qu'ils sont la même classe C++ "worker CJobManager")
   ; `Uds` → `0x801235070` (classe différente, attendu).
4. `0x800D924F0` désassemblé en entier (~4000 octets) : c'est la VRAIE
   boucle worker CJobManager — dans le même voisinage d'adresses que les
   `ret=` déjà vus sur les traces `sema.signal`/`sema.wait-block` pour
   `name='CJobManager'` (`0x800D93368`, `0x800D92CF9`, etc.), confirmant
   qu'on est dans le bon sous-système.
5. À `0x800D92C90`-`0x800D92CB3` : boucle de dequeue avec spin-lock
   (`lock cmpxchg`) sur un slot de job (`[r12]`), lisant le descripteur à
   `r13` (`[r13+r14*8+30h]` = userdata du job) puis
   **`call qword [r13+r14*8+28h]`** — le vrai site de dispatch par job,
   function pointer + userdata par emplacement. `r13`/la table de
   descripteurs est indexée depuis une base fixe par thread
   (`lea rcx,[rel 8051FE008h]` pour JobWorkerLow1 — dans la même région
   que son `arg=`), confirmant une file de jobs PRIVÉE par worker (design
   work-stealing), pas une file globale unique.

### Où ça s'arrête cette session

Identifier PRÉCISÉMENT si un job « soumettre le préambule » existe un
jour dans une de ces files nécessiterait soit : (a) tracer en live la
valeur du pointeur de fonction à CHAQUE exécution de
`call qword [r13+r14*8+28h]` sur tout un run (aucun mécanisme de hook
générique sur du code natif exécuté directement n'existe dans SharpEmu —
il faudrait soit une nouvelle sonde par polling de l'emplacement mémoire,
soit un hook plus invasif), soit (b) une recherche BEAUCOUP plus large
des 3 adresses de buffer comme référence INDIRECTE (via une struct
intermédiaire pointée par le job, pas comme immédiat littéral dans le
code — recherche déjà faite mais seulement sur 2 fenêtres de code
étroites, pas sur l'ensemble du binaire). Les deux options n'ont pas été
tentées cette session, par arbitrage coût/bénéfice : 7 sessions
cumulées de désassemblage manuel ont déjà consommé un temps très
important pour une piste qui reste, à chaque nouveau niveau, cohérente
avec la conclusion déjà posée (rien n'écrit jamais ces 3 adresses) sans
jamais localiser LE point de rupture exact.

Fait acquis, robuste, qui NE dépend d'aucune des sessions précédentes :
**le système de jobs CJobManager fonctionne normalement** (workers
tournent, dequeue par CAS, dispatch par pointeur de fonction — aucun
signe de corruption ou de blocage structurel dans le mécanisme lui-même).
Le problème est donc bien, très probablement, l'absence totale de
création d'un job spécifique (cause A du plan de mission), plutôt qu'un
job bloqué (cause B/C) — un mécanisme de dispatch sain n'expliquerait pas
un blocage en aval sans une trace de wait supplémentaire, qu'on n'a
jamais observée pour ce chemin précis.

## 2026-07-17 (8e session) — Fix EOP int_sel vérifié (mesure live), stall inchangé

### Fix appliqué et vérifié par mesure directe du depcount

Hypothèse ré-instruite (contredisant partiellement la conclusion « INVALIDÉE »
de la 6e session, qui s'était basée sur UN run) : gating des kevent EOP par
pool d'adresse (`IsCpuVisibleLabel`) au lieu du champ `int_sel` réel du
paquet `release_mem`. Nouveau protocole de vérification : lecture directe
du `depcount` (`[V+0x2C44]`, V=0x806981F00) sur le process vivant, toutes
les 1 ms, plutôt que de la déduction depuis les logs.

- **Avant fix** : depcount dérive à `-3` vers t=10,6s et reste négatif —
  confirme la sur-livraison sur CE run (le run de la 6e session ne
  l'avait simplement pas montré ; comportement non-déterministe selon
  le timing JIT, cohérent avec le reste de la session).
- **Fix** (`AgcExports.cs`, `ApplySubmittedReleaseMem` +
  `ApplySubmittedStandardReleaseMem`) : le kevent n'est délivré que si
  `interruptSelection`/`interrupt` (champ réel du paquet, ordinal 24
  bits) est non nul — sémantique matérielle correcte, plus de gating
  par adresse.
- **Après fix** : depcount reste à `0` sur tout le run observé (150s).
  Correction réelle et vérifiée, indépendamment du reste du diagnostic.

### Mais le stall persiste — reconfirmé, kicks jamais écrits

Après le fix, les 3 kicks (`0x2011669FE0`/`0x2011831650`/`0x2011882330`)
sont toujours à `0x0` en mémoire au moment du gel (lu en live). Poke
manuel des 3 adresses à `1` (`WriteProcessMemory`) : les 3 queues
compute reprennent (le mécanisme de reprise fonctionne), mais se
resuspendent sur des fences suivantes du même pipeline — toujours
aucune `submission=7/8`. Confirme, par une méthode indépendante
supplémentaire, la conclusion de la 6e/7e session : le problème n'est
pas la livraison du signal, c'est qu'aucun code observé n'écrit jamais
ces 3 adresses.

### Deux fausses pistes closes cette session

- **Descripteur supplémentaire ignoré par `SubmitAcb`** : dump de
  `rdx/rcx/r8/r9` + 0x60 octets du paquet au-delà du `{base,size}`
  actuellement lu. Résultat : `rdx`/`rcx` portent de petits entiers
  (compteurs), pas des adresses ; le paquet contient des pointeurs de
  structures internes (mutex, canari `0xC0DEC0DECAFEBA00`, l'adresse
  DCB graphics déjà connue à +0x40) — **aucune trace des 3 adresses
  préambule nulle part dans cette structure**. L'hypothèse « argument
  ignoré » est éliminée proprement.
- **`sceAgcAcbJump` (NID `e1DFTg+Sd8U`) absent du code** : résolution
  des thunks GOT de `0x800AB4A30` (la fonction de pompe des 3 queues
  qui marchent, session 5) montre que ce NID n'a aucune implémentation
  dans `AgcExports.cs`. Vu comme piste prometteuse, réfutée après
  relecture du garde conditionnel (`cmp byte[rbx+0xA8],1` /
  `test r14b,r14b`) : les deux sites d'appel sont skippés dans toutes
  les conditions observées, cohérent avec son absence totale des logs
  `unresolved` malgré des centaines d'appels à cette fonction. NID
  réellement non implémenté mais non exercé sur ce chemin — à laisser
  de côté sauf preuve contraire.

### État : nouveau mur, conforme au protocole (stop, pas de fix à l'aveugle)

Le fix `int_sel` est gardé (correction réelle, vérifiée, indépendante
du reste). Le mur frame-2 reste au même point qu'en fin de 7e session :
CJobManager fonctionne normalement, aucun code (HLE, PM4, ou store CPU
brut) n'a jamais été observé écrire les 3 adresses de kick. Piste
suivante non tentée (coût élevé) : hook live sur
`call qword [r13+r14*8+28h]` (site de dispatch par job identifié
session 7) pour capturer la valeur du pointeur de fonction à chaque
exécution sur un run complet.

## 2026-07-17 (9e session) — Hook debugger réel : les deux sites de dispatch CJobManager ne s'exécutent JAMAIS

### Méthode : vrai débogueur Windows plutôt qu'un patch aveugle

Au lieu d'un trap flag/DR7 (mécanisme déjà connu dangereux sur ce codebase,
cf. `yotei-watchpoint-failfast-not-tf`), implémentation d'un vrai débogueur
externe via `DebugActiveProcess`/`WaitForDebugEvent`/`ContinueDebugEvent`
(script scratchpad `hookdispatch.ps1`) : les événements de debug passent
par le canal noyau, jamais par les gestionnaires d'exception internes du
process cible (VEH de SharpEmu, SEH du CLR) — aucun conflit possible avec
le fatal UnmanagedCallersOnly déjà documenté. INT3 logiciel posé sur le
site d'appel, contexte du thread lu via `GetThreadContext` à l'arrêt,
octet original restauré et ré-armé après chaque coup ; `DebugSetProcessKillOnExit(false)`
+ `finally` garantissent que le jeu continue de tourner après détachement
même en cas d'erreur du script (bug corrigé en cours de route : un crash
du script ENTRE `WaitForDebugEvent` et `ContinueDebugEvent` laisse un
thread bloqué à vie côté cible → mort silencieuse du process, observé
2 fois avant d'ajouter un `try/catch` interne qui garantit l'appel à
`ContinueDebugEvent` sur toute erreur de traitement d'événement).

### Résultat : zéro exécution des deux sites de dispatch candidats

Deux fonctions testées séparément, chacune sur 2-3 runs complets couvrant
tout le boot (attaché dès le lancement du process, avant toute exécution
de code jeu, jusqu'à bien après le flip figé) :

- `0x800D92CAE` (`call qword ptr [r13+r14*8+0x28]`, site multi-jobs avec
  spin-lock CAS, celui décrit en session 7) : hook posé et vérifié stable
  (lecture de l'octet original `0x43`=REX.XB confirmée, relecture après
  écriture confirme `0xCC`, aucune réversion silencieuse détectée par
  sondage périodique) — **0 coup sur 3 runs à couverture complète**, y
  compris pendant la rafale `sema.signal count=10 waiters=10` (ligne
  ~5350-5360 de chaque log) qui précède immédiatement les 3 soumissions
  qui marchent.
- `0x800D92C5D` (`call qword ptr [rbx+8]`, dispatcher à un seul job,
  fonction voisine plus simple repérée dans la même désassemblage) :
  octet original `0xFF` confirmé (cohérent avec l'encodage `FF /2`) —
  **0 coup également**.

### Ce que ça élimine, ce que ça laisse ouvert

Ce résultat négatif est robuste (mécanisme de hook vérifié fonctionnel à
chaque étape : l'exception d'attache initiale est toujours reçue, l'octet
s'installe et reste en place, le process continue de tourner et de logguer
normalement pendant toute la fenêtre observée). Conclusion : **les deux
sites de dispatch de jobs trouvés en session 7 par désassemblage statique,
bien que réels et atteignables selon le graphe d'appel statique
(`0x800D924F0 → 0x800D92C70`, marqué noreturn par `ud2`), ne sont pas
empruntés par les threads JobWorker vivants pendant le boot de Yotei**.
Les rafales `sema.signal name='CJobManager'` visibles dans les logs
réveillent donc les workers pour une activité qui passe par un tout autre
chemin de code, non encore identifié — soit une autre fonction membre de
la même classe C++ worker, soit un appel inline sans passer par ces deux
sites précis. La piste « CJobManager comme famille de fonctions à
explorer » reste ouverte, mais les deux candidats les plus prometteurs de
session 7 sont maintenant formellement écartés par mesure directe, pas
par supposition.

**Livrable réutilisable** : `hookdispatch.ps1` (scratchpad) est un
mécanisme de hook générique fonctionnel (attache, pose de breakpoint,
lecture de contexte, restauration propre) — réutilisable tel quel pour
tester d'autres sites de code candidats sans repartir de zéro sur
l'ingénierie du débogueur.

### État de session : mur atteint, arrêt conforme au protocole

Aucun nouveau site de dispatch n'a été identifié pour relancer le hook
dans l'immédiat. Le fix `int_sel` (8e session) reste en place et vérifié.
Le mur frame-2 reste non résolu : CJobManager fonctionne normalement,
les 3 adresses de kick ne sont jamais écrites, et les deux mécanismes de
dispatch de job les plus plausibles ne sont jamais empruntés. Prochaine
piste, non tentée : élargir le hook à d'autres offsets de la classe
worker (ex. les autres slots vtable au-delà de `+0x58`) ou tracer les
sites d'appel de `0x8016cbaa0`/`0x8016cb130` (les primitives de sommeil
observées dans la boucle « pas de job disponible » de `0x800D92C70`) pour
voir combien de temps les workers y restent bloqués et ce qui les réveille
réellement.

## 2026-07-17 (10e session) — Identité des primitives de sommeil résolue ; la technique de hook INT3 elle-même mise en doute

### `0x8016cbaa0`/`0x8016cb130` identifiées : `sceKernelWaitSema`/`sceKernelUsleep`

Résolution GOT→NID (outil `resolve_got.py`, parse PT_DYNAMIC/JMPREL sur
l'eboot déchiffré) : `0x8016cbaa0 = Zxa0VhQVTsk = sceKernelWaitSema`,
`0x8016cb8d0 = 1jfXLRVzisc = sceKernelUsleep` (confirmé par hash SHA1,
cf. [[nid-resolution-method]]). La boucle « pas de job » de `0x800D92C70`
attend donc un vrai sémaphore standard, pas une primitive maison.

Run tracé (`SHARPEMU_LOG_SEMA=1`) : les workers de cette boucle précise
(`ret=0x800D92CF9`, juste après l'appel WaitSema) attendent sur
`sceKernelCreateSema` **handle=2** (nommé `CJobManager`, distinct du
handle=3 déjà noté en session 4 qui lui n'a jamais reçu le moindre
signal). Handle=2 reçoit une vraie rafale de 33 signaux tôt dans le run
(motif cyclique 5/2/1, `ret=0x800D93368` = le site déjà identifié en
session 4 comme le point où le thread d'interruption AGC signale
CJobManager après que son compteur de dépendances atteigne zéro, plus
2 autres call-sites jamais vus avant : `ret=0x800D93476` et
`ret=0x8010D4CEB`), **puis plus aucun signal pour le reste du run** —
les 10 workers restent bloqués en `wait-block` à partir de ce point.
Reproduit à l'identique sur 2 runs indépendants (91 wait-block / 33
signal, arrêt net à la même paire de compteurs).

### Désassemblage complet de la fonction d'enqueue (`0x800D92590`)

Appelée depuis `0x800D93363` (le site de dépendance-à-zéro déjà connu),
elle pousse un pointeur de continuation sur `[r12+0x10]` de la MÊME
table globale que celle lue par la boucle worker (base confirmée
identique par calcul : `0x8051FE008`, calculée indépendamment des deux
côtés — `lea rcx,[rip+0x446b341]` côté worker vs
`lea rdi,[rip+0x446aca5]` côté enqueue, même résultat), puis signale le
sémaphore à `[r12+8]` avec `signalCount=min(waiterCount,10)`. Structurellement,
push et signal ciblent donc la même structure de queue que celle que les
workers dépilent — la mécanique de notification/enqueue est saine et
couplée correctement dans ce code.

### Tentative de corrélation live push/pop : résultat invalidant la méthode, pas le mécanisme du jeu

Objectif : poser 3 breakpoints INT3 (les 2 sites d'écriture
`[r12+0x10]=rax` dans `0x800D92590`, et le site de lecture
`r13=[r12+0x10]` dans la boucle worker, `0x800D92D06`) pour vérifier si
push et pop touchent réellement la même adresse `r12` en pratique.

Trois faux départs méthodologiques corrigés en cours de route (documentés
pour la prochaine session, pas pour re-belliger) :
1. SharpEmu se relance en enfant `--sharpemu-mitigated-child` (CFG/CET
   désactivés, nécessaire au JIT natif) — le PID retourné par un
   lancement naïf est celui du **lanceur**, pas du process avec la
   mémoire guest mappée (confirmé : lire à l'adresse cible sur le
   lanceur donne `state=MEM_FREE`). Fix déjà documenté ailleurs
   ([[quake-run-command]]) : `SHARPEMU_DISABLE_MITIGATION_RELAUNCH=1`
   évite complètement le relaunch et règle aussi la troncature des logs
   redirigés — aurait dû être vérifié en mémoire AVANT de relancer à
   l'aveugle.
2. Une fois attaché au bon PID, 3 tentatives (mid-run) ont donné 0 hits
   sur push1/push2/pop. Corrélation avec le propre log sema du même
   process : la rafale de 33 signaux est finie depuis longtemps à chaque
   fois (voir ci-dessus, rafale unique et précoce) — les breakpoints
   étaient posés APRÈS la fenêtre où l'activité pouvait avoir lieu, pas
   un vrai résultat négatif.
3. Hook relancé correctement, attaché dès l'événement d'attache initial
   (avant toute exécution de code jeu, même protocole que la 9e session),
   fenêtre de 240s confirmée recouvrir intégralement la rafale (91
   wait-block / 33 signal dans le log du MÊME process pendant la fenêtre
   du hook) : **toujours 0 hits sur push1/push2/pop**. Test de sanité
   supplémentaire : breakpoint sur `0x800D92CF9` seul (le retour d'appel
   WaitSema, dont l'exécution est prouvée par le log sema du même run) —
   **0 hits également**, avec vérification que l'octet 0xCC posé ne se
   fait pas silencieusement écraser entre les passages (aucun message
   `REVERTED` loggé).

Ce dernier résultat est le plus important : `0x800D92D06` est à quelques
instructions en ligne droite après `0x800D92CF9` (juste un
`lock cmpxchg` de spinlock entre les deux) — il est structurellement
impossible que le CPU atteigne l'un sans passer par l'autre peu après,
sauf branchement vers le chemin de contention (`0x800D92D20`, qui boucle
et retombe de toute façon sur `0x800D92D06` une fois le spinlock
acquis). Avoir 0 hits sur une adresse dont l'exécution est prouvée par
ailleurs indique un problème de fiabilité de la technique de breakpoint
logiciel INT3 elle-même dans ce contexte (10 threads en forte contention
sur le même spinlock juste à cet endroit → course plausible entre
restauration/réarmement de l'octet 0xCC et l'arrivée d'un autre thread),
**pas une preuve que le code ne s'exécute jamais**. Ceci remet en cause
la confiance à accorder aux conclusions « 0 hits » de la 9e session
(`hookdispatch.ps1`, mêmes limites structurelles, jamais testées contre
une adresse à exécution prouvée).

**Conclusion pour la suite** : la technique de hook INT3 via
`DebugActiveProcess` telle qu'utilisée depuis la session 9 n'est pas
fiable sur du code à forte contention multi-thread dans ce host managé
— à ne plus utiliser pour trancher des questions de type « ce code
s'exécute-t-il jamais » sans un test de sanité préalable (breakpoint sur
une adresse à exécution déjà prouvée par un autre canal). Pistes plus
fiables pour la suite : lecture différentielle de mémoire à haute
fréquence (déjà fait pour les 3 adresses de kick, fiable — pas de
threading impliqué côté lecteur) plutôt que des breakpoints d'exécution;
ou un vrai point d'arrêt matériel (DR7) accepté comme risqué
(cf. [[yotei-watchpoint-failfast-not-tf]]) mais au moins non sujet à
cette classe de course.

## 2026-07-17 (10e session, suite) — Hypothèse « soumettre les 3 préambules orphelins » CONFIRMÉE, mur déplacé d'un cran

### Format des 3 en-têtes confirmé par lecture mémoire live

Lecture directe (`ReadProcessMemory`, process stable post-flip) des 3
adresses orphelines identifiées en session 5 : chacune est un en-tête de
8 qwords `{base, limite=base+0x10000, curseur, label-cible≈+0x20,
vtable=0x8009F5750 (commun aux 3), self, état=4, ?}`. `curseur` donne le
nombre d'octets réellement écrits par le constructeur du jeu
(`0x8012F2B94`) — `dwordCount = (curseur-base)/4` : 1033/259/127 dwords
pour compute[72]/48/56 respectivement.

### Diagnostic implémenté et vérifié : `SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES=1`

Nouveau code dans `AgcExports.cs` (`EnsureOrphanPreambleForceSubmit` /
`RunOrphanPreambleForceSubmit`, gardé par la variable d'env, **diagnostic
jetable et spécifique à Ghost of Yotei — pas un fix**) : 8s après le
premier `DriverSubmitAcb`, lit `base`/`curseur` des 3 en-têtes en direct
et les fait passer par le pipeline de soumission RÉEL de SharpEmu
(`EnqueueSubmittedDcb`/`DrainResumableDcbs`, le même code que
`sceAgcDriverSubmitAcb`), sur des `ownerHandle` synthétiques (900001-3)
pour ne pas percuter les queues déjà trackées.

**Résultat (`log_yotei_forcesubmit1.txt`) : les 3 fences sont réellement
écrites par le contenu authentique du jeu** (`agc.dcb.release_mem
dst=0x2011669FE0/0x2011831650/0x2011882330 ... wrote=True`), **et les 3
queues bloquées (compute[72]/48/56) reprennent leur exécution**
(`agc.queue_resumed`, confirmé pour les 3). L'hypothèse posée en session
5 est donc **prouvée, pas juste plausible** : ces 3 buffers pré-construits
par le jeu contiennent le vrai travail attendu ; il ne manque que l'appel
de soumission, jamais localisé malgré 9 sessions de désassemblage.

### Nouveau mur, un cran plus loin : `0x20000003C0` (compute[56] uniquement)

Après reprise, compute[72] et compute[48] ne re-stallent pas (soit
terminés, soit en attente non tracée) ; compute[56] seul se resuspend sur
une NOUVELLE fence `0x20000003C0` (même pool CPU-visible
`0x2000000000-0x2000010000`), même signature `producer=none-observed` —
confirme le motif « pipeline multi-étages » déjà noté en session
« Le frame graph complet, prouvé par pokes mémoire live » : débloquer un
étage révèle le suivant, pas un unique verrou.

Poke direct de `0x20000003C0=1` sur le process vivant (test rapide,
`WriteProcessMemory`) : **n'a PAS réveillé compute[56]** dans la fenêtre
observée (contrairement aux pokes précédents qui avaient marché
immédiatement) — soit ce fence a une sémantique différente (pas un simple
kick booléen), soit la re-vérification du wait registry n'est déclenchée
que par un nouvel événement de soumission (pas de scan spontané), pas
encore déterminé. Frame 2 **toujours pas rendue** à la fin de cette
session — mais le mur a mesurablement bougé, avec une preuve directe
(pas une hypothèse) que la voie est la bonne : trouver comment le jeu
soumet réellement ses préambules (ou, à défaut, généraliser ce
force-submit à un mécanisme non hardcodé) reste le chemin le plus court
vers la frame 2 observé à ce jour.

**État du code** : le diagnostic reste dans l'arbre, gardé par
`SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES=1`, clairement commenté comme
non-générique — à retirer une fois la vraie cause (pourquoi le jeu ne
soumet jamais ces 3 buffers) trouvée, ou remplacé par un fix générique
si elle s'avère être un bug HLE commun plutôt qu'un trou de couverture
Yotei-only.

## 2026-07-17 (10e session, suite 2) — Généralisation : découverte automatique, 13 buffers enchaînés, 34 queues reprises

### Le diagnostic à 3 adresses en dur remplacé par un suivi générique

Au lieu de deviner les adresses une par une (ce qui venait de révéler un
4e buffer orphelin, `0x804F80370` → cible `0x20000003C0`, trouvé
par hasard dans les logs), le diagnostic suit maintenant **toute** cible
`sceAgcCbReleaseMem` construite par le jeu (`TrackCbReleaseMemTarget`,
appelé depuis `CbReleaseMem`, table `_cbReleaseMemTargets: cible→en-tête`)
et, dès qu'une queue réelle se bloque (`ParseSubmittedDcbCore`, juste
après `GpuWaitRegistry.Register`) sur une adresse déjà vue comme cible
d'un tel builder, soumet automatiquement ce buffer.

### Bug de conception intermédiaire trouvé et corrigé : identité de `CpuContext.Memory`

Première version : un moniteur d'arrière-plan sondant
`GpuWaitRegistry.SnapshotInRange` toutes les ~500ms avec le `ctx.Memory`
capturé au premier `DriverSubmitAcb`. Résultat : `stalled=0` en
permanence alors que `GpuWaitRegistry.Count` (global) montrait bien des
attentes actives. Cause confirmée par logging de `GetHashCode()` des
deux côtés : **chaque thread natif a sa propre instance
`CpuContext.Memory`, non `ReferenceEquals` entre threads**, même si toutes
écrivent/lisent la même mémoire guest physique — `SnapshotInRange`/
`CountForMemory` filtrent par référence et ne voyaient donc jamais les
attentes enregistrées par un autre thread. Fix : abandon du sondage
d'arrière-plan, hook direct dans `ParseSubmittedDcbCore` au moment exact
où le wait s'enregistre — même thread, même `ctx`/`gpuState`, plus
d'ambiguïté d'identité possible.

### Résultat (`log_yotei_autosubmit5.txt`) : 13 buffers, 34 reprises, zone mémoire jamais atteinte avant

Run complet avec le hook corrigé : **13 buffers orphelins découverts et
soumis automatiquement en cascade** (chaque reprise réveille la queue
suivante, qui référence à son tour un nouveau buffer jamais vu), contre 3
trouvés manuellement en session 5. **34 `agc.queue_resumed`** au total.
Le pipeline progresse dans une toute nouvelle région mémoire
(`0x2014xxxxxx`, jamais atteinte dans aucune session précédente).

### Nouveau palier : fences à producteurs multiples (`ref=2`, `ref=3`)

4 attentes restent non résolues en fin de run, dont 2 d'une nature
différente des précédentes : `label=0x2000000380 ref=2` et
`label=0x2000000480 ref=3` — ces fences attendent un COMPTEUR qui doit
atteindre 2 ou 3 (plusieurs producteurs distincts doivent chacun
incrémenter), pas une simple écriture booléenne. Le mécanisme actuel n'en
satisfait qu'un producteur sur plusieurs. Deux `orphan_preamble_skip`
notés au passage (non fatals) : `0x806981F88` (probablement le même objet
driver `V=0x806981F00` déjà identifié en session 4 — faux positif, pas un
vrai buffer orphelin) et `0x8044CEC08` (déjà connu en session 5 comme
alias d'une ring RÉELLEMENT active, pas un orphelin — cohérent, correctement
ignoré plutôt que de planter).

**Frame 2 toujours pas rendue**, mais le mur a bougé de façon spectaculaire
par rapport au début de cette session (3 buffers/3 reprises → 13
buffers/34 reprises, nouvelle région mémoire atteinte). Prochaine étape
naturelle, non tentée : étendre `TryForceSubmitOrphanPreamble` pour gérer
les fences à `ref>1` (nécessite de suivre TOUS les producteurs connus
d'une même cible, pas juste le dernier, et de les soumettre tous avant de
compter sur la reprise).

## 2026-07-17 (11e session, mode autonome) — Multi-producteur + drain différé + fenêtre de ring

### Multi-producteur implémenté, puis crash d'assert → cause = réentrance

`_cbReleaseMemTargets` passé de `cible→dernier header` à `cible→liste de
headers` (les fences compteur `ref=2/3` exigent le release_mem de CHAQUE
producteur). Premier run (`log_yotei_multiprod1.txt`) : crash
`Debug.Assert(state.IsSuspended)` dans `ResumeSuspendedDcb`. Cause lue
dans la pile : le force-submit inline s'exécutait PENDANT
l'enregistrement du wait (`HandleSubmittedWaitRegMem`), or
`PumpSubmittedQueue` ne pose `state.IsSuspended` qu'au retour du parse
(`state.IsSuspended = ParseSubmittedDcb(...)`) — si le release_mem de
l'orphelin satisfait le wait tout juste enregistré, `DrainResumableDcbs`
résume une queue dont le parse est encore sur la pile. Les sessions
précédentes passaient par chance (fenêtre plus étroite).

### Fix : drain différé (`_orphanPreamblePendingTargets`)

L'enregistrement du wait ne fait plus que noter la cible ; la soumission
réelle (`DrainPendingOrphanPreambles`, boucle jusqu'à liste vide pour la
cascade) s'exécute uniquement depuis des sites sans parse sur la pile :
fin de `DriverSubmitDcb`/`DriverSubmitAcb` et boucle du moniteur
`MonitorGpuWaits`. Le moniteur re-propose aussi toutes les adresses de
wait vivantes au tracker (`SnapshotInRange` — fiable ici : son
`ctx.Memory` est cloné du thread enregistreur), couvrant le cas
« producteur construit APRÈS le wait ». Vérifié
(`log_yotei_multiprod2/3.txt`) : plus aucun assert, cascade OK.

### Run 3 : 300 s sans crash, compteurs qui avancent, flip #1 stable

`log_yotei_multiprod3.txt` : 13 orphelins soumis, 31 reprises, flip #1
présenté, #5,8M imports, fin par timeout (pas de crash). Les fences
compteur avancent réellement (0x480 : 0→2 via compute[82] soumis 2×).
État final : les waits restants attendent tous la VALEUR SUIVANTE des
mêmes compteurs (0x380==2, 0x480==3, 0x3A0==2, 0x420==2, 0x20==2…).

### Découverte structurelle : les builders sont des RINGS croissants, pas des one-shots

Preuve par les logs `cb_release_mem` : le MÊME header construit data=1
puis data=2/3 plus tard, à des `cmd=` différents (voire au MÊME `cmd=`
après wrap — buf 0x804F11F20 : data=1 et data=3 tous deux à
0x201160C044). Notre soumission one-shot base→curseur rate tous les
ajouts ultérieurs → les compteurs plafonnent à 1 incrément. Fix
implémenté : soumettre la fenêtre COMPLÈTE du ring (base→limite, header
qword[1], garde ≤1 Mo) — le parseur exécute jusqu'au contenu écrit puis
se parque sur le premier mot nul (`SuspendOnUnwrittenRingWord`), et le
moniteur le reprend quand le jeu ajoute — exactement la sémantique des
queues réelles. Réserve à vérifier : comportement au wrap (le ring se
réécrit) et sur les ajouts non contigus (0x2011→0x2014).

### Fatal UCO = bottleneck d'itération

2 runs sur 4 tués tôt par le fatal CLR UnmanagedCallersOnly préexistant
(exit 6, non déterministe, fenêtre mobile avec le layout JIT). Non
traité (hors mission), mais il coûte ~la moitié des runs.

### Fenêtre de ring v1 : DEUX dangers réels découverts par les crashs

Après passage au window-submit (base→limite), 6 runs consécutifs morts en
exit 6 (contre des survies intermittentes avant). Deux causes réelles
identifiées dans les logs :

1. **`0x804F11F20` est le ring du DCB GRAPHIQUE du jeu** (base
   0x201160C000 == l'`addr=` de `driver_submit_dcb`, resoumis à chaque
   frame). `sceAgcCbReleaseMem` est une API générique : les « buf »
   tracés incluent les VRAIS rings (graphics, computes) ET les orphelins.
   Le stall sur 0x2000000020 arrivant AVANT la première soumission
   graphique du jeu, l'auto-submit a soumis le ring graphique en double.
   Mitigation : `RecordGameSubmittedRange` (plages soumises par le jeu
   via DriverSubmitDcb/Acb) + check de chevauchement → skip
   `aliases game-submitted ring`. Trou résiduel connu : ordre
   stall-avant-submit (dupliqué borné, écritures release_mem
   idempotentes — valeurs absolues data_sel=2, pas des incréments).
2. **Le suivi chunk_advance (+0x10000 contigu) est FAUX pour les
   builders** : ils sautent vers des chunks NON contigus (~49 Mo
   d'écart observé). Le window-submit de 16384 dwords suivait les
   sentinelles dans de la mémoire étrangère (park observé à
   0x201162C3E4, 2 chunks au-delà de la fenêtre) → parsing de garbage →
   crashs. Fix : `IsForceSubmittedRing` sur les queues orphelines —
   pas de chunk-advance (les changements de chunk sont couverts par la
   re-soumission sur changement de base du header,
   `_orphanPreambleSubmittedBase`), ET park sur mot nul dès l'offset 0
   (la condition `FollowedChunkAdvance` originelle empêchait le park en
   première fenêtre → un header nul terminait la soumission en silence
   et les ajouts intra-chunk étaient perdus).

Dumps CLR du fatal UCO capturés (`crashdumps/yotei_uco_*.dmp`,
DOTNET_DbgEnableMiniDump) mais `dotnet-dump analyze` ne résout pas les
symboles SharpEmu (méthodes « Unknown ») — piste d'analyse garée,
coût/bénéfice défavorable pour la mission frame-2.

### Fatal UCO devenu quasi systématique ce soir — investigation dédiée

~20 runs consécutifs morts en exit 6, groupés sur DEUX fenêtres
reproductibles : (a) `videoout.register_buffers2` + création des 10
JobWorkers (~#12K imports, AVANT toute soumission orpheline — donc pas
causé par le code orphelin), (b) zone post-flip (#85K-870K, variable).
Faits mesurés :
- Event log Windows : chaque crash = « internal error in the .NET
  Runtime at IP 0x00007FFD8B5C2561 » — **même IP coreclr à chaque
  fois** (offset +0x2D2561, exit 0x80131506) → un unique check runtime.
- Sans `SHARPEMU_LOG_AGC` : crash identique (le tracing est hors de
  cause).
- **`DOTNET_TieredCompilation=0` : passe les deux fenêtres** (~#870K
  imports au lieu de #12K) mais finit par crasher aussi — le tiering
  (backpatching des entrées UCO à la promotion tier0→tier1 pendant la
  tempête de threads) aggrave fortement mais n'est pas toute l'histoire.
- Seules entrées `[UnmanagedCallersOnly]` Windows du process :
  `RunPrologue`/`RunEpilogue` (NativeWorker). Les handlers VEH passent
  par des stubs délégués (`Marshal.GetFunctionPointerForDelegate`),
  classe différente de failfast.
- L'en-tête de NativeWorker.cs documente déjà les fenêtres résiduelles :
  « import dispatch, VEH redirection » sur les threads workers.
A/B baseline (sans FORCE_SUBMIT_ORPHAN_PREAMBLES, tiering par défaut) :
**2/2 runs survivent les 240 s complètes** (exit 124, flip #1 présenté —
le flip n'a jamais eu besoin des orphelins, c'est un acquis des commits
62d543d/1cda5ed). Conclusion : le fatal est déclenché par le déplacement
de timing/JIT qu'introduit le chemin force-submit, pas par un bug
fonctionnel unique — cohérent avec la sensibilité au layout JIT
documentée depuis la session 4.

### Deux hypothèses de fix UCO testées et invalidées (revertées)

1. **Stubs délégués au lieu d'UnmanagedCallersOnly** sur
   `RunPrologue`/`RunEpilogue` : 3/3 runs crashent à l'identique, même
   message — le check de transition reverse-P/Invoke de coreclr est
   UNIFIÉ entre stubs délégués et UCO, le swap ne contourne rien.
   REVERTÉ (git checkout NativeWorker.cs).
2. **Drain orphelin déplacé hors des threads guest** (uniquement dans
   `MonitorGpuWaits`, thread CLR pur) : 3/3 crashs aussi — le « gros
   travail managé dans la fenêtre d'import » n'est pas (seul) le
   mécanisme. Le déplacement est GARDÉ (plus propre de toute façon),
   mais il ne suffit pas.

### Hypothèse courante (en test) : corruption par contenu de ring périmé

Différence structurelle avec la session 10 (13 soumissions orphelines
SANS crash, cursor-bounded) : toutes les variantes de ce soir parsent
AU-DELÀ du curseur d'écriture (fenêtre complète, ou jusqu'au premier
mot nul). Or les rings se réécrivent SANS être remis à zéro (prouvé :
data=1 et data=3 au même cmd=) → entre curseur et premier zéro traînent
des paquets périmés de frames précédentes → les exécuter corrompt
l'état guest → fautes en aval sur les threads workers → le failfast UCO
n'est qu'un symptôme. Fix en test :
- soumission **incrémentale bornée au curseur** : delta
  [dernierCurseur, curseur) par header, re-scanné par le moniteur
  (couvre les ajouts data=2/3 sans jamais parser du périmé) ; changement
  de base = nouveau chunk = redémarrage du suivi ;
- **gate vtable** : seuls les headers de classe orpheline confirmée
  (vtable 0x8009F5750, lectures live session 10) sont soumis — exclut
  structurellement le ring graphique et les autres vrais rings du jeu,
  y compris dans le trou d'ordre « stall avant première soumission ».

## 2026-07-18 (12e session, mode autonome) — FRAME 2 PRÉSENTÉE

### Vérifications préalables : trois fausses pistes refermées avec preuves

1. **Table PM4 complète** : tracker inconditionnel d'opcodes inconnus
   (`agc.dcb.unknown_opcode`, une fois par valeur distincte) → zéro
   opcode inconnu sur un run entier. `IT_COND_INDIRECT_BUFFER` n'existe
   pas dans le stream de Yotei.
2. **Shaders amont et `-KRzWekV120` hors de cause** : owners 82/32/83
   tracés de bout en bout (logs non échantillonnés via les nouveaux
   `agc.driver_submit_*_call`). Owner 82 TERMINE malgré 2 shaders
   intraduisibles (un échec de traduction saute le dispatch, ne bloque
   pas la queue) ; leurs release_mem n'écrivent aucune des 3 kicks.
   `-KRzWekV120` = finalisation PrimState avant draw graphique, pas un
   mécanisme de dépendance inter-queue.
3. **Gate JobManager** : déjà clos par le watchpoint matériel d'une
   session antérieure (pointeur jamais réécrit — pas un compteur).

### Percée 1 : delta cursor-bounded + DOTNET_TieredCompilation=0 = plus de fatal UCO

La combinaison jamais testée (le fix anti-contenu-périmé de la session
11 + tiering désactivé) survit **2/2 runs de 240 s complets** avec
FORCE_SUBMIT_ORPHAN_PREAMBLES=1, là où delta2 seul crashait 3/3 et
tiering-off seul finissait par crasher aussi. Les deux moitiés du
mécanisme (backpatching tier0→tier1 pendant la tempête de threads +
exécution de paquets périmés) devaient être supprimées ENSEMBLE.

### Percée 2 : la frame 2 était enregistrée et SOUMISE — bloquée par notre park ring-tail

Avec la cascade orpheline active, le jeu progresse réellement : les
fences compteur avancent, le thread principal sort de son spin, et le
jeu **enregistre et soumet un 2e cycle de frame complet** — SubmitDcb #2
sur un ring TOUT NEUF (0x201450C000, avec dcb_reset_queue en tête),
les 6 queues compute resoummises avec de nouvelles adresses, et le
paquet set_flip #2 (index=3 arg=2) à 0x201452C3CC. Mais la 2e
soumission n'était JAMAIS parsée : la queue graphics restait parquée au
ring tail de l'ANCIEN ring (0x201162C3E4, SuspendOnUnwrittenRingWord),
un mot que le jeu n'écrira jamais puisqu'il est passé au ring suivant.
`PumpSubmittedQueue` retournait immédiatement sur IsSuspended → la
frame 2 attendait éternellement derrière un park périmé.

### Fix : supersede du park ring-tail (committé 1e8bd35)

Un park ring-tail est notre attente synthétique du write-pointer CP —
valide uniquement tant que le jeu continue d'append au MÊME ring. Une
nouvelle soumission explicite sur la queue signifie que le jeu est passé
à un ring frais : le park est abandonné (waiter retiré via le nouveau
`GpuWaitRegistry.TryRemoveByState`, soumission active clôturée,
`agc.dcb.ring_tail_superseded` tracé) et la nouvelle soumission se
parse. Les suspends WAIT_REG_MEM authentiques du jeu ne posent jamais
`RingTailParkAddress` et ne sont jamais abandonnés.

### Résultat : flip #2 capturé et présenté, frame 3 en cours

Run de validation (240 s, exit 124, zéro crash) :
- `agc.dcb.ring_tail_superseded addr=0x201162C3E4` → parse du ring 2
- `vk.flip_capture version=2 submission=21 addr=0x5007190000` (nouveau
  display buffer, différent de la frame 1)
- `vk.flip_retired version=1` après la capture v2 → l'image de la
  frame 1 a été remplacée à l'écran par la frame 2 : **présentée**
- 3e cycle entamé au timeout : owners 32/82/83 resoummis, le jeu
  ré-enregistre dans 0x201160C000 (double-buffering des rings de
  frame, frame 3 réutilise le ring de la frame 1)

### Réserves / prochaine session

- FORCE_SUBMIT_ORPHAN_PREAMBLES=1 reste requis (le « pourquoi le jeu ne
  soumet pas ses builders lui-même » n'est toujours pas élucidé — le
  diagnostic est devenu de facto le mécanisme fonctionnel).
- Cadence très lente : ~2 frames en 240 s (latences du moniteur de
  drain 1-16 ms par étape de cascade + tiering désactivé). Prochain
  chantier naturel : cadence des flips.
- Run de contrôle tiering PAR DÉFAUT lancé en fin de session pour
  savoir si TieredCompilation=0 est réellement indispensable au fix
  (résultat non encore connu à l'écriture de cette note).

## 2026-07-18 (12e session, suite) — CYCLE DE FRAMES CONTINU : 5 flips présentés

### Mesure : timestamps t= sur toutes les traces AGC

Les chaînes de dépendances série s'étendent sur des dizaines de
secondes ; sans timing par ligne, impossible d'attribuer la latence
(production du jeu vs re-soumission émulateur). Constat immédiat : le
paquet producteur d'un kick attendu 48 s était CONSTRUIT à t=3,5 —
toute la latence était de notre côté.

### Trois bugs de la machinerie orpheline, corrigés (commit 0f077e8)

1. **Cycle de vie des arènes de builder** : les builders alternent
   entre arènes par frame (frame N et N+2 partagent la même). (a) Un
   changement d'arène entre deux drains du moniteur orphelinait
   définitivement la fin non soumise de l'arène sortante — qui
   contenait les seuls producteurs des kicks de la frame suivante.
   (b) Un builder revenant sur une arène déjà vue (même base, curseur
   RÉGRESSÉ) était lu comme « pas de nouveau contenu ». Fix :
   snapshot du header à chaque sceAgcCbReleaseMem ; changement de
   base → la tranche restante de l'arène fermée est mise en file pour
   le prochain drain (les rings ne sont jamais remis à zéro, le
   contenu reste valide) ; régression de curseur → restart de la
   tranche depuis la base.
2. **« Unreadable at trigger time » = transitoire, pas permanent** :
   le builder 0x806AB69D0, déclenché avant sa construction, était
   empoisonné (0,0) au boot puis détenait les seuls producteurs de la
   frame 3. Loggé une fois, reste éligible.
3. **Deux classes de builder acceptées** : recensement live de tous
   les callbacks cmd_alloc_full → deux vtables (graphics 0x8009F5750,
   utilisée AUSSI par le vrai builder du ring graphique ; compute
   0x800AB4550). Le gate mono-vtable n'a jamais été la vraie
   protection anti-double-submit (c'est le check d'alias des plages
   game-submitted) ; il ne doit exclure que les objets étrangers.

### Identité mémoire canonique (même commit)

Chaque worker natif enveloppe la même mémoire virtuelle partagée dans
son propre TrackedCpuMemory : clé SubmittedGpuState et filtres
GpuWaitRegistry sur la référence brute → état GPU FRACTURÉ par thread
(observé : un SubmitAcb frame-2 atterrit dans un état neuf, sequence
repartie à 1, invisible du moniteur). Déroulage des chaînes
ICpuMemoryWrapper partout où l'identité mémoire sert.

### Résultat : régime permanent ~15,7 s/frame

`log_yotei_arenaclose_1.txt` : 5 flip_capture (t=5,6 / 77,7 / 95,1 /
110,8 / 126,5), alternance parfaite des deux display buffers et des
deux rings de frame (supersedes 0x201162C3E4 ↔ 0x201452C3E4), 53
fermetures d'arènes flushées. Le draw composite de chaque frame
échantillonne bien la sortie compute de la frame (writer chain câblée).

### Mur suivant (frame 6) : gate CPU, pas GPU

Après le 5e flip : ZÉRO wait GPU non résolu (tous compteurs de fence à
5), threads vivants (~3K imports/s de polling, trylock BUSY habituel),
mais plus AUCUNE émission de travail GPU. Dernier acte : release_mem
data=5 + write_data increment sur 0x2000000060. C'est un gate côté
logique de jeu (attente d'un événement flip/pacing ? d'un compteur
qu'on ne délivre pas ?), PAS une dépendance GPU — classe de problème
différente de tout ce qui précède dans cette session.

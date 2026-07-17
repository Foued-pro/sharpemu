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

### Hypothèses en attente (dans l'ordre)

1. Contenu blanc : vérifier la traduction du display buffer 4K category=1
   (compression ? le rendu écrit-il dans les buffers enregistrés ?) et
   pourquoi une seule présentation (attente vblank/flip suivant ?).
2. Les waits compute restants (labels 0x2011831650/0x2011669FE0/0x2000000480)
   — vérifier s'ils se résolvent maintenant que graphics submits + flips
   existent.

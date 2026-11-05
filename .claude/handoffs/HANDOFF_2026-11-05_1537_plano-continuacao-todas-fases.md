# Handoff — Smart Colliders: plano de continuação até fechar todas as fases

> Gerado: 2026-11-05 15:37 · Branch: main · Último commit: 960025a Initial check-in (nada da ferramenta foi commitado)

## Objetivo
Ferramenta Unity Editor pessoal **Smart Colliders** (`com.reclabs.smart.colliders`, pacote UPM embutido) para geração/edição automática de colliders, inspirada no asset "Collidify" (Bitwise Games) — implementação original, sem copiar código/ícones/identidade visual. Requisito do usuário: "tudo e mais um pouco", "quero bem bonito", foco 3D mas 2D também, PhysX padrão, qualidade > simplicidade.

## Estado Git
- Branch: `main`
- Não commitado (tudo da sessão):
  - novos: `CLAUDE.md`, `Packages/com.reclabs.smart.colliders/` (pacote inteiro), `Assets/SmartColliders Generated/` (mesh banks criados por bakes manuais do usuário), `Assets/Imports/` (modelos do usuário: tanque, Gripen — NÃO criados por nós), `ProjectSettings/SceneTemplateSettings.json`
  - modificados: `.gitignore` (+ `TestResults.xml`), `Packages/manifest.json` (+ `testables`), `Packages/packages-lock.json`, `Assets/Settings/*_RPAsset.asset`, `ProjectSettings/ProjectSettings.asset`, `ProjectSettings/ShaderGraphSettings.asset` (alterações do Unity)
  - `Assets/TutorialInfo/Icons/URP.png` 24 KB → 130 bytes: **pointer Git LFS no lugar do binário**; rodar `git lfs install && git lfs pull` antes do 1º commit.
- Commits da sessão: nenhum (usuário não pediu).

## Decisões do usuário (fixas)
| Tema | Decisão |
|---|---|
| Escopo | Tudo (todas as fases abaixo) |
| Física | 3D foco, 2D também; PhysX padrão (sem BNG/DOTS) |
| Decomposição | **V-HACD v4 E CoACD, selecionável** (Low/Medium = V-HACD, High/Ultra = CoACD) |
| Plataformas nativas | **Windows x64 + macOS universal** |
| Primitivo vs hull | Algoritmo escolhe primitivo quando melhor |
| UI | EditorWindow (foco) + Inspector + Scene view |
| Batch | Sim, mas **nunca automático no import** |
| Destino do bake | Usuário escolhe; **padrão = no objeto que tem a malha (Source Objects)** para seguir peças animadas |
| Preview | Sim, antes de aplicar, com Undo |
| Pacote | `com.reclabs.smart.colliders`, Unity mín. **6000.3** |
| Visual | Painéis escuros, accent **laranja #FFA53D** (ações/ferramenta ativa) + **violeta #9B7BFF** (seleção). Não usar verde-limão do Collidify |

## Feito nesta sessão
### Pacote `Packages/com.reclabs.smart.colliders/`
| Arquivo | O quê |
|---|---|
| `package.json` | deps Burst 1.8.29, Mathematics 1.3.3, Collections 2.6.6, physics, physics2d |
| `Runtime/RecLabs.SmartColliders.asmdef`, `Editor/RecLabs.SmartColliders.Editor.asmdef` (rootNamespace `RecLabs.SmartColliders.EditorTools`), `Tests/Editor/*.asmdef` | 3 assemblies; `AssemblyInfo.cs` com InternalsVisibleTo |
| `Runtime/Geometry/GeometryUtils.cs` | eps relativo, base ortonormal, PCA (Jacobi), canonicalização de eixos p/ ~identidade |
| `Runtime/Geometry/ConvexHull3D.cs` | quickhull Burst com conflict lists, cap de vértices progressivo (129 ⇒ ≤254 tris), Volume, Planes, SignedDistance, Raycast |
| `Runtime/Geometry/MinEnclosing.cs` | Welzl esfera/círculo iterativo, hull 2D monotone chain, min-area rect |
| `Runtime/Geometry/ColliderShape.cs` | struct shape (Sphere/Capsule/Box/ConvexHull), SDF, volume, SampleSurface, Raycast, Bounds |
| `Runtime/Geometry/PrimitiveFitter.cs` | FitSphere, FitBox (candidatos world/PCA/normais do hull + min rect), FitBoxOriented, FitCapsule, FitCapsuleOriented |
| `Runtime/Geometry/FitMetrics.cs` | FitReport, CostOf/TierOf, EvaluatePrimitive/EvaluateHull (IoU aninhado), EvaluateEdited (volumes exatos + interseção amostrada) |
| `Runtime/Geometry/ShapeFitter.cs` | ShapeFitSettings, FitAll (avalia os 4 sempre), Select (Allow* só filtra auto), `ShapeFitJob` Burst `CompileSynchronously` |
| `Runtime/Authoring/SmartColliderAuthoring.cs` | componente; `BakeTarget {SameObject, ChildContainer, SourceObjects}` padrão SourceObjects |
| `Runtime/Authoring/SmartColliderPart.cs` | part = TriangleRanges + 4 candidatos + 4 reports + hull; PaletteColor |
| `Runtime/Authoring/SmartColliderMeshBank.cs`, `TriangleRanges.cs` | banco de malhas de hull (sub-assets); codificação (start,count) |
| `Editor/Tools/SourceGeometry.cs` | junta MeshFilter + SMR (BakeMesh) em espaço local do authoring; Signature; CollectPoints; SourceIndexOf |
| `Editor/Tools/SmartColliderOperations.cs` | Generate Single/PerMesh, Refit, RefitAll, SetType, SetAuto, **RemoveParts**, IsStale, Fit |
| `Editor/Tools/SmartColliderRefine.cs` | FitToMesh, AlignToAxes, ResetParts, Reevaluate(recordUndo), Translate/Rotate/SetShape/ScaleHull, PartReference |
| `Editor/Tools/SmartColliderBaker.cs` | Bake/ClearBaked/BakeAndRemove; BakeIntoContainer; **BakeOnHost** (conversão exata authoring→host, filho rotacionado só c/ escala uniforme, ConvexFallback); WriteHullMesh in-place |
| `Editor/Tools/SmartCollidersProjectSettings.cs` | ScriptableSingleton `ProjectSettings/SmartColliders.asset`, GeneratedFolder |
| `Editor/Tools/StripAuthoringOnBuild.cs` | remove authoring das cenas no build |
| `Editor/Shaders/SmartCollidersPreview.shader` | `Hidden/SmartColliders/Preview` fresnel, pass0 visível / pass1 x-ray |
| `Editor/UI/ColliderPreviewRenderer.cs` | preview duringSceneGui (fill + wire, seleção violeta, hover) |
| `Editor/UI/PreviewMeshes.cs` | malhas unitárias + cache de hull (feature edges) com sweep |
| `Editor/UI/PartSelection.cs`, `PartPicker.cs` | seleção de parts por sessão; raycast analítico |
| `Editor/UI/SmartColliderEditTool.cs` | EditorTool: picking, modos Select/Move/Rotate/Resize, teclas 1–4, Esc, comandos SoftDelete/Delete/SelectAll/FrameSelected |
| `Editor/UI/SceneHud.cs` + `SmartCollidersHud.uss` + `HudIcons.cs` | HUD UI Toolkit flutuante ancorado na seleção: card de métricas + toolbar 40px com ícones vetoriais Painter2D + Bake |
| `Editor/UI/SmartColliderAuthoringInspector.cs` | inspector IMGUI funcional (provisório), Edit in Scene, Preview/X-Ray, linhas clicáveis, Refit, × remover |
| `Editor/UI/SmartColliderMenu.cs` | `GameObject/Smart Colliders/Generate Colliders` + `Tools/Smart Colliders/...` |
| `Editor/UI/SmartCollidersTheme.cs` | cores/tamanhos + PreviewPrefs |
| `Tests/Editor/GeometryTests.cs`, `AuthoringTests.cs`, `RefineTests.cs` | ~46 testes EditMode |
| deletados | `Editor/UI/SmartColliderGizmos.cs` (substituído pelo renderer), `Editor/UI/SmartColliderEditOverlay.cs` (substituído pelo HUD) |

### Raiz
- `CLAUDE.md` — spec + arquitetura (manter atualizado a cada fase).
- `.gitignore` + `TestResults.xml`; `Packages/manifest.json` + `"testables": ["com.reclabs.smart.colliders"]`.

## Decisões técnicas
- **Geometria no assembly Runtime** (Burst, sem deps de editor) → permite geração em runtime futura.
- **Namespace editor `RecLabs.SmartColliders.EditorTools`** — `.Tools` colide com `UnityEditor.Tools`; segmento `.Editor` colide com `UnityEditor.Editor`.
- **Todos os 4 candidatos guardados por part** → conversão de tipo instantânea, sem refit.
- **Fits sempre envolventes** (enclosing); hull com cap é *inner* (quickhull progressivo pelo ponto globalmente mais distante).
- **Accuracy** = IoU volumétrico vs hull completo da part (aninhado ⇒ min/max volume); editado ⇒ `EvaluateEdited` (I/(Vs+Vr−I), I amostrado em grade no frame do shape).
- **Custo físico heurístico**: sphere 1, capsule 1.25, box 1.5, hull 3+V/32; tiers ≤24 Light, ≤72 Medium, ≤240 Heavy, senão Heavy+. Provisório.
- **Bake**: primitivos Unity não giram → filho rotacionado; escala não-uniforme + rotação ⇒ MeshCollider convexo (sem shear). Malhas do bank atualizadas in place e nunca deletadas (Undo seguro).
- **Preview** via `Graphics.DrawMeshNow` em duringSceneGui (agnóstico de pipeline).
- **HUD** em UI Toolkit no `sceneView.rootVisualElement`; coords GUI→painel via `GUIUtility.GUIToScreenPoint − sceneView.position.position`.
- **Delete** em parts: interceptar `ValidateCommand/ExecuteCommand` "SoftDelete"/"Delete" no OnToolGUI (senão Unity apaga o GameObject).

## Tentativas descartadas
- Overlay `ITransientOverlay` com botões de texto → usuário: "muito pequeno e não fácil de interagir" → trocado por HUD flutuante 40px ancorado na seleção.
- Grid IoU sobre AABB da união (res 28) → erro 7% com fronteira desalinhada → trocado por volumes exatos + interseção amostrada dentro do shape (<2%).
- `float.CompareTo` em comparer Burst → trocado por comparações explícitas.
- Gizmos (`DrawGizmo`) para preview → limitado (sem capsule/fill de hull) → renderer próprio.
- Bake só em SameObject/Container → usuário quer collider na peça (porta/trem animado) → `SourceObjects`.

## Estado atual
- **Funciona (testado em batch, 42/42 na última rodada completa)**: Fases 1–3 até antes do HUD/Delete/SourceObjects.
- **Usuário validou visualmente**: preview, HUD (screenshot do tanque), inspector com 43 parts (Gripen).
- **Compilou no Editor aberto mas NÃO testado em batch**: HUD, Delete/RemoveParts, comandos (SelectAll/Frame), × no inspector.
- **Escrito mas NÃO compilado/testado**: modo `BakeTarget.SourceObjects` (`BakeOnHost`, `ConvexFallback`, `SourceIndexOf`) + 4 testes novos (3 SourceObjects + RemoveParts). Unity estava aberto e não recompilou (só recompila com foco).
- Benchmark: FitAll 100k pontos ≈ 40 ms (Burst).

## Pendências (ordem de execução)

### 0. Estabilizar (antes de qualquer fase nova)
1. Com o projeto **fechado** no Unity: rodar suíte inteira em batch (comando abaixo); corrigir falhas de `SourceObjects`/`RemoveParts`.
2. Se o projeto estiver aberto: focar o Unity, ler `%LOCALAPPDATA%\Unity\Editor\Editor.log` por `error CS` (ordenar por número de linha — o log contém outros projetos também).
3. Validar no Gripen com o usuário: Bake Target = Source Objects, Bake, girar DoorMain_L → collider acompanha.
4. `git lfs install && git lfs pull`; perguntar ao usuário se quer commitar (sugestão: branch `feat/smart-colliders`, commits por fase; não incluir `Assets/Imports/` sem perguntar).

### Fase 3 — fechar resíduos
5. HUD: rotação por eixo (campos X/Y/Z em graus + botões ±90°/flip) e toggle Local/Global para o handle de rotação/move.
6. Ferramenta **Split/Paint part**: pincel na Scene que pinta triângulos (raycast na malha fonte → índice de triângulo) para nova part; atualiza `triangleRanges` (usar `TriangleRanges.FromSorted`) e refit das duas parts.
7. **Merge parts** (selecionadas → uma part, união das ranges, refit).
8. Re-bake reaproveitar o container existente (não recriar GameObject); limpeza segura de malhas órfãs do bank (só via ação explícita "Clean Unused Meshes").

### Fase 4 — UI final ("bem bonito")
9. **EditorWindow principal** (`Window/Smart Colliders`, UI Toolkit + USS próprio): lista de objetos/parts com busca, painel de parâmetros, métricas agregadas, botões Generate/Bake, ligação bidirecional com PartSelection e Scene.
10. **Popovers de parâmetros** no HUD (clicar segurando/seta no botão): Auto Generate (Quality, Max Colliders presets 2/5/10/25/50/100, Auto Merge + Tolerance presets 0.01/0.05/0.1/0.25/0.5, Async + Yields), Mesh/Hull (Max Hull Vertices ≤129 com descrição), título + descrição + atalho em cada popover.
11. Inspector em UI Toolkit no mesmo visual (substituir `SmartColliderAuthoringInspector` IMGUI).
12. Ícones próprios para EditorTool/component (gerar via Painter2D → textura ou VectorImage), estados de hover/press com transições já no padrão do `SmartCollidersHud.uss`.
13. Tooltips ricos e atalhos documentados; preferências (Preferences/Smart Colliders: opacidade, espessura, x-ray, cores).

### Fase 5 — Decomposição (maior item)
14. Verificar toolchain: Visual Studio C++ + CMake no Windows; perguntar ao usuário sobre acesso a Mac (senão entregar Win agora, Mac depois via CI/GitHub Actions macos-latest).
15. Native plugin `Plugins~/native/` (fonte fora do import) com C API estável: `sc_decompose(points, tris, params, callbacks progress/cancel) → hulls`. Integrar **V-HACD v4** (header-only, BSD) e **CoACD** (MIT, CMake, preprocess manifold). Saídas: `Plugins/Windows/x86_64/smartcolliders_native.dll`, `Plugins/macOS/smartcolliders_native.bundle` (universal).
16. Wrapper C# P/Invoke (`Editor/Decomposition/NativeDecomposer.cs`), rodar em thread/job com **pause/resume/cancel** (pause = flag checada nos callbacks), overlay de progresso no HUD (tempo, nº colliders, Pause/Cancel).
17. `GenerateMode.Decompose`: cada hull da decomposição vira part (triangle assignment: triângulo → hull que contém o centróide/menor SDF), refit com ShapeFitter (primitivo quando melhor), **Auto Merge** por tolerância (merge de vizinhos se IoU do merged ≥ limiar), **Max Colliders**.
18. Accuracy agregada vs **malha real** (voxelização/winding number da fonte) para o card do HUD.
19. Fallback puro C# se DLL ausente (decomposição por split recursivo de planos PCA + hull) para não quebrar em plataformas sem binário.

### Fase 6 — Skinned
20. Partição por bone dominante (bone weights do SMR), parts com `boneIndex`; bake com colliders filhos dos bones (BakeTarget SourceObjects ⇒ bone); preview seguindo pose atual; teste com rig simples gerado em código.

### Fase 7 — Batch
21. Aba Batch na EditorWindow: arrastar prefabs/pastas/seleção, presets (`SmartColliderPreset` ScriptableObject com ShapeFitSettings + modo + bake target), rodar com progresso/cancel, editar prefabs via `PrefabUtility.LoadPrefabContents`/`SaveAsPrefabAsset`, relatório final. **Nunca AssetPostprocessor.**

### Fase 8 — 2D
22. Fonte: SpriteRenderer (outline física do sprite `Sprite.GetPhysicsShape`) e meshes planas; fits Box2D/Circle2D/Capsule2D (reusar MinEnclosing 2D + min rect), PolygonCollider2D, decomposição convexa 2D (Hertel-Mehlhorn ou Bayazit) → compound + CompositeCollider2D opcional; preview/edição na Scene 2D.

### Fechamento
23. Documentação do pacote (README com fluxo Generate → Refine → Bake, atalhos, settings), CHANGELOG, `samples~` com cena demo; tag v1.0.0; instruções de uso via git URL nos outros projetos.

## Contexto crítico p/ retomar
- Unity: `Y:\Unity\6000.3.17f1\Editor\Unity.exe` (há outras versões em `Y:\Unity\`; outros projetos costumam estar abertos — `TheAverageShapeOf`, `virtus`).
- Testes headless (só com o projeto fechado):
  `"/y/Unity/6000.3.17f1/Editor/Unity.exe" -batchmode -projectPath . -runTests -testPlatform EditMode -assemblyNames RecLabs.SmartColliders.Editor.Tests -testResults "$(pwd)/TestResults.xml" -logFile Logs/test-run.log`
  Resultado: `grep -o '<test-run[^>]*>' TestResults.xml`; falhas: PowerShell `[xml]` + `//test-case[@result='Failed']`.
- Python não está instalado (usar sed/perl/PowerShell). `grep` é reescrito pelo hook RTK (ripgrep) — use `rtk proxy grep` ou a ferramenta Grep para regex com `\|`.
- Arquivos `.cs` do pacote podem ter BOM (sed em `^using System;$` falhou uma vez).
- Pastas geradas em testes: `Assets/__SmartCollidersTests` (apagada no TearDown). Bank padrão: `Assets/SmartColliders Generated/`.
- `ShapeFitSettings.Default`: MaxHullVertices 64, MinAccuracy 0.8, CostWeight 0.02, MinThickness 0.01.
- Enum `BakeTarget` ordem serializada: SameObject=0, ChildContainer=1, SourceObjects=2 (só acrescentar no fim).
- Perfil do usuário: respostas curtas em pt-BR, código direto, prioridade a visual/UX ("bem bonito"), não quebrar o que funciona; quando algo "ficar feio" tratar como bug prioritário.

## Como retomar
Numa sessão nova: `/handoff resume .claude/handoffs/HANDOFF_2026-11-05_1537_plano-continuacao-todas-fases.md` ou "leia .claude/handoffs/HANDOFF_2026-11-05_1537_plano-continuacao-todas-fases.md e continue da pendência 1".

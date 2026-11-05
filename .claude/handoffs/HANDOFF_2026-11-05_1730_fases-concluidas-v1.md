# Handoff: Smart Colliders, fases 3 a 8 concluídas (v1.0.0)

> Gerado: 2026-11-05 17:30 · Branch: main · Último commit: 960025a Initial check-in (nada commitado nesta sessão)

## Objetivo
Executar o plano `HANDOFF_2026-11-05_1537_plano-continuacao-todas-fases.md` até o fim, de forma autônoma.

## Estado Git
- Nada foi commitado; o pacote inteiro continua untracked.
- Snapshots em refs ocultas, sem tocar branch nem index. Cada uma contém `Packages/com.reclabs.smart.colliders` e `CLAUDE.md`:
  - `snapshots/00-stable`
  - `01-phase3`
  - `02-decomp-skinned`
  - `03-native`
  - `04-ui-batch-2d`
  - `05-polish` (última)
- Para restaurar: `git checkout snapshots/05-polish -- Packages/com.reclabs.smart.colliders CLAUDE.md`.
- `.github/workflows/native-plugins.yml` não está em nenhuma snapshot.

## Feito
- **Fase 3**:
  - Rotação por eixo (±90° e campo exato) com Local/Global (tecla X).
  - Paint/Split: brush, grow, fill connected, add to part.
  - Merge (tecla M).
  - Re-bake reaproveita o container.
  - Clean Unused Meshes.
- **Fase 4**:
  - Janela `Window/Smart Colliders` com abas Object e Batch.
  - Inspector em UI Toolkit (mesmo painel da janela).
  - Popover Generate no HUD (tecla G).
  - Overlay de progresso da decomposição na Scene, com pause/resume/cancel.
  - Página de Preferences.
  - Ícones vetoriais rasterizados (janela, tool e componentes); ícone de gizmo da Scene oculto.
  - Métrica "mesh fill" (volume da malha fechada ÷ volume dos colliders).
- **Fase 5**:
  - Engine built-in (voxel + Burst).
  - V-HACD nativo para Win x64 e macOS universal (build com zig).
  - CoACD via CMake e workflow do GitHub Actions; não testado localmente.
  - `HullMerge` (Auto Merge / Max Colliders) aplicado aos hulls nativos.
- **Fase 6**: Per Bone para skinned meshes, com pose acompanhando preview, picking e bake.
- **Fase 7**:
  - Batch com `SmartColliderPreset`.
  - Entradas: prefabs (`LoadPrefabContents`), pastas e objetos de cena (com Undo).
  - Execução cancelável, com relatório; sprites usam o caminho 2D.
  - Sem `AssetPostprocessor`.
- **Fase 8**: 2D com `SmartCollider2DAuthoring`.
  - Modos: Auto, Circle, Box, Capsule, Polygon e Decompose.
  - Decompose: cortes por extensão de aresta, depois ear clipping + Hertel–Mehlhorn, depois merge.
  - Preview na Scene e bake com Undo.
- **Fechamento**:
  - README, CHANGELOG, `Native~/README.md`.
  - Sample `Samples~/DemoSceneBuilder`.
  - `package.json` em 1.0.0.
  - CLAUDE.md atualizado.
- **Bugs corrigidos**:
  - Double-dispose no `NativeDecomposer` (campos `readonly` faziam o Dispose agir sobre uma cópia).
  - `Finish` idempotente.

## Testes
- 91/91 EditMode no test host `Y:/Github-PA/SmartColliders-TestHost`.
- Capturas visuais conferidas em `Captures/*.png`: hud, hud_generate, hud_progress, window, window_decompose, window_bake, window_batch, window_empty, window_2d, scene_2d, inspector.

## Pendências (precisam do usuário)
1. **Validação no Gripen** (modelos reais em `Assets/Imports/`): Generate Per Mesh e Decompose (Medium/High), Refine e Bake.
2. **Commit**: rodar `git lfs pull` antes. Sugestão: commitar `Packages/com.reclabs.smart.colliders`, `CLAUDE.md`, `.github/` e `Packages/manifest.json`, sem `Assets/Imports/`. Depois, tag `v1.0.0` para a URL git.
3. **CoACD**: rodar o workflow `native-plugins` no GitHub e copiar os binários para `Plugins/`, mantendo os `.meta`.
4. **macOS**: o binário universal (V-HACD) foi gerado, mas não foi testado num Mac.
5. **README**: substituir `<owner>` na URL git.

## Contexto crítico
- Nunca encerrar o Unity pelo nome da imagem. Nesta sessão, `taskkill /IM Unity.exe` fechou o Editor principal às ~16:52; ele foi reaberto ao final. Encerrar só o PID do test host.
- `capture.sh` trava até o timeout quando há erro de compilação: rodar `run-tests.sh` antes.
- Sprites criados em runtime não aceitam `OverridePhysicsShape`/`OverrideGeometry`, e o shape de fallback é convexo. Por isso os testes 2D passam o contorno via `SmartCollider2D.Bake(a, outline)` (overload interno).
- Anotações de gizmo: `GizmoUtility.SetIconEnabled` só funciona depois de `AnnotationUtility.GetAnnotations()` (via reflexão).

## Como retomar
`/handoff resume .claude/handoffs/HANDOFF_2026-11-05_1730_fases-concluidas-v1.md`

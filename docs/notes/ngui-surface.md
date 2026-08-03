# NGUI surface — what the connect screen (M10c) needs

Paraphrased names/signatures only, same discipline as the other notes files. No game code
is reproduced here.

## Where it lives, and why this file exists

NGUI's types — `UIInput`, `UILabel`, `UIWidget`, `UIPanel`, `UISprite`, `UICamera`,
`NGUITools`, `UILocalize` — are compiled into **`Assembly-CSharp-firstpass.dll`**, not
`Assembly-CSharp.dll`. For the whole of M0–M10a the repo decompiled only the latter, so
every statement anyone made about `UIInput` was inferred from *call sites* in game code
(`CIHelperSavePreviewSidebar`, `CIViewReporter`, `CIViewNavigable`) rather than read from
the class. That is corroboration, not an API, and this project has a four-instance history
of plausible readings becoming settled facts and then becoming bugs.

Decompile it before touching UI:

```
DOTNET_ROLL_FORWARD=Major ilspycmd -p -r vendor/Managed \
  -o decompiled-firstpass vendor/Managed/Assembly-CSharp-firstpass.dll
```

352 files. Gitignored, like `decompiled/`. The `DOTNET_ROLL_FORWARD` is needed because
`ilspycmd` targets .NET 8 and only 9 is installed here.

`PBAndJ.Mod.csproj` now references the assembly, so NGUI types are available to mod code.

## The traps, in order of how expensive they are to find later

### `UILocalize` will silently overwrite a cloned widget's text

A `MonoBehaviour` that sits alongside a `UIWidget`. It re-runs on `Start` **and on every
`OnEnable`** once started — so it fires again each time a screen is shown, not only when it
is built. Behaviour:

- If its `key` is empty it **adopts the label's current text as the key**, then looks that up.
- It resolves the key through NGUI's own `Localization` and assigns the result.
- For a `UILabel` that is the `label` of a parent `UIInput`, it writes the input's
  `defaultText` (the placeholder) instead of the label text. For a `UISprite`, it rewrites
  the sprite name.

So a cloned field or button that carries one will have our text replaced by a lookup of our
own text — usually blank — every time the screen is re-enabled. **Destroy any `UILocalize`
on a cloned subtree, or give it a key we control.** Check for it in the `pbj.ui-dump` output.

Note this is NGUI's localization, and is *separate* from the game's own (`Txt.Get` /
`DataManagerText`), which is what `CIViewPauseRoot.RefreshLocalization` uses. Both can write
a label; a cloned widget can be exposed to either.

### `UIInput.savedAs` is a built-in PlayerPrefs autosave — keep it away from the passphrase

`UIInput` has `savedAs` plus `SaveValue()` / `LoadValue()`, which persist a field's contents
to `PlayerPrefs` under that name. Convenient, and exactly wrong for the passphrase: it would
store it unconditionally, with no opt-in and no way to honour the "remember" tickbox. A
cloned input may arrive with `savedAs` already set from its donor — **clear it on every
clone**, and let `ConnectSettings` own persistence.

## `UIInput`

- Statics: `current` (the input being processed), `selection` (the focused one; the game
  nulls this to release focus, and `CINavUtility` checks it to suppress menu navigation while
  typing).
- `label` — the `UILabel` it renders through. **A clone's `label` is only remapped if the
  label is inside the copied hierarchy**; if the donor's label sits outside the subtree you
  clone, the clone writes into the original's label. Verify with `pbj.ui-dump` before cloning.
- `value` — get/set the text. Setting it notifies; `Set(text, notify)` gives control over that.
- `defaultText` — placeholder shown when empty.
- `inputType` — `Standard` / `AutoCorrect` / **`Password`**. So the passphrase field can be
  masked properly; the earlier fallback of "or the plaintext warning covers it" is unnecessary.
- `validation` — `None` / `Integer` / `Float` / `Alphanumeric` / `Username` / `Name` /
  `Filename`, plus an `onValidate` per-character hook. `Integer` suits the port field, but it
  is a convenience only — `ConnectForm` still validates, because the widget rule is not tested
  and `int.TryParse` would accept a leading sign anyway.
- `characterLimit`, `hideInput`, `submitOnUnselect`, `onReturnKey` (`Default`/`Submit`/`NewLine`).
- `onChange` / `onSubmit` are `List<EventDelegate>` — append, do not assign, or the donor's
  own handlers are silently dropped.
- `onSelect` is an `Action<UIInput>`; `onUpArrow` / `onDownArrow` are plain `Action`s, useful
  for tabbing between the three fields without touching the gamepad nav system.
- Methods: `Submit()`, `UpdateLabel()`, `RemoveFocus()`, `ForceSelection(bool)`,
  `ProcessEvent(Event)`.

## `UILabel`

`text`, `fontSize`, `alignment`, `overflowMethod` (with `overflowWidth` / `overflowHeight` /
`overflowEllipsis`), `multiLine`, `spacingX`/`spacingY`, `supportEncoding` (the `[b]…[/b]`
markup the game's menu labels use), gradient fields, `trueTypeFont`.

## `UIWidget`

`width`, `height`, `color`, `depth`, `raycastDepth`, `rawPivot`, `isVisible`,
`hasBoxCollider`; `SetDimensions(w, h)` and `SetRect(x, y, w, h)`.

## `NGUITools`

`AddChild(parent)` and `AddChild(parent, prefab)` (extension methods on both `GameObject`
and `Transform`), `AddChild<T>(parent)`, `AddWidgetCollider(go)`, `FindInParents<T>(go)`.
`AddChild` is the NGUI-correct way to parent — it sets the layer and resets the local
transform, which raw `Instantiate` + reparent does not.

## Answered by `pbj.ui-dump`, on a running game

Everything below is observed, not inferred.

### `CIViewReporter` is inactive at rest — so it is safe to clone

`View_Report` reports `activeSelf=False, activeInHierarchy=False` while the game sits at the
title screen. Unity does not run `Awake` on an instantiation of an inactive object, so a
clone can have its `CIViewReporter` component destroyed and ours added *before* it is
activated — and `CIViewReporter.ins` is never hijacked. Cloning it while active would have
broken the game's own bug reporter, silently, in a way that would surface weeks later.

`CIViewDialogConfirmation` is likewise inactive at rest.

### The `UIInput` and its `UILabel` are the same GameObject

```
Label_Description  [Transform, UILabel, BoxCollider, UIInput(onChange->CIViewReporter.OnInputChange)]
```

So the reference-remapping worry does not arise: cloning that one object yields a
self-contained field. It is the donor to use for all three connect-screen inputs.

**But it carries a serialized `onChange` pointing at `CIViewReporter.OnInputChange`,** and a
clone inherits it — every keystroke would call into the game's bug reporter. Clear `onChange`
on each clone before use. (Clear `savedAs` too, per the warning above.)

### `CILabel`, not `UILocalize`, is what overwrites cloned labels here

No `UILocalize` appears anywhere in these views. The game uses its own equivalent:

- `CILabel.Start()` calls `ApplyLibraryValue()`, which assigns
  `label.text = DataManagerText.GetText(textSector, textKey)` and logs a warning when the key
  resolves empty.
- `DataManagerText` keeps a `registeredLabels` list and re-applies every one of them on a
  language change.

A newly activated clone runs `Start` on the *next* frame — after whatever text we set — so a
cloned label carrying `CILabel` reverts to the donor's text and warns. **Destroy `CILabel` on
cloned label subtrees**, or point its `textSector`/`textKey` at something we own.

Menu buttons are exempt: their `Label` child is a plain `UILabel` with no `CILabel`, which is
why setting the text from a `RefreshLocalization` postfix holds. The reporter's labels are
not — `Label_Header`, `Label_Subheader`, `Label_Hint` and the button labels all carry it.

### Donors worth using, and one to avoid

| Need | Use | Components |
|---|---|---|
| Text field | `Container_Comment/Label_Description` | `UILabel, BoxCollider, UIInput` |
| Tickbox | `Button_Toggle_IncludeSave` | `CIButton, UIWidget, BoxCollider, UIEventListener` + `Sprite_Icon` + `Label` |
| Push button | `Button_Bug` / `Button_Idea` / `Button_Feedback` | `CIButton, UIWidget, BoxCollider, UIEventListener` |
| Panel/background | `Container` + `Sprite_Background_Main` | `UIWidget, BoxCollider` / `UISprite` |

Avoid `Button_Dialog_Next` as a donor — it drags a `CIHelperOverworldEventOption` along.

### The title menu builds its own buttons, and ours is indistinguishable

Our entry dumps as

```
pbj_multiplayer  [Transform, CIButton, UIWidget, CIAnimTimelineHelper, BoxCollider, CIHelperPauseButton, UIEventListener]
```

— component-for-component identical to `load` and `options`, because the game constructed it
from the same prefab off our `ButtonLink`. List order is screen order, so the link is
inserted after the one keyed `load` rather than appended.

## Answered by `pbj.ui-spike-input`, typing on a running game

### Typing works at the title screen, unaided

A cloned `UIInput` parented under `CIViewPauseRoot`'s transform took focus from
`ForceSelection(true)` and received every keystroke, with **no input context entered** and
nothing done about `UICamera` or Rewired. `UIInput.selection` and `UICamera.selectedObject`
both became the clone. So the connect screen needs no input-context machinery to work.

`labelIsOwn=True` — Unity remapped the clone's `UIInput.label` to the clone's own `UILabel`,
confirming that cloning the single `Label_Description` object yields a self-contained field.
Clearing the inherited `onChange` worked: nothing reached `CIViewReporter.OnInputChange`.

### But there is no input *exclusivity*, and that is the real finding

While the field held focus, it also captured everything typed into the **dev console**,
including the backtick that opens and closes it — which arrived as a literal `` ` `` in the
value. Worse, the traffic went both ways: console commands got garbled into things like
`pbkj`, and Quantum Console raised `Command 'pbkj' could not be found`.

The cause is that both sides read raw input directly and neither knows about the other:
NGUI's `UIInput` polls Unity input every frame while selected, and Quantum Console does its
own reading. The game's input contexts gate neither, which is why entering one would not have
helped — and why the spike deliberately tested the unaided case first.

**Consequence for the connect screen:** drop focus while the console is up.
`QuantumConsole.Instance.IsActive` is public and is the guard to poll — the pump in
`Heartbeat.Update` already runs every frame at the main menu. This is a development-time
concern only; a second player never opens the console, which is the entire point of the
screen. But it will bite whoever is testing, on their first attempt, every time.

This also corrects an assumption in the M10c checklist: "F1 and Alt+B do not fire while a
field is focused" is **false** by default. Nothing gates them; if that behaviour is wanted it
has to be built.

## Two `UIInput` behaviours that cost a build each

### There is no placeholder text — the em-dash is hardcoded

`UpdateLabel` computes, for an empty field:

```
if (value is empty)  text = (isSelected ? "" : "—")
```

`mDefaultText` is never consulted on that path. So `defaultText` cannot be used as a
placeholder in this NGUI build, and an empty field always renders `—`. That is the game's
own convention and looks native, so the connect screen keeps it and puts its hints on the
captions instead. Setting `defaultText` looks like it works right up until you look.

### Style the label *before* touching the input, or the styling is silently reverted

`UIInput.Init()` caches the label's alignment into `mAlignment`, and `UpdateLabel` restores
`label.alignment = mAlignment` on every redraw. `Init()` is triggered lazily by the first
`UIInput` property setter touched (`value`, `defaultText`, …). So:

- style the `UILabel` (pivot, alignment, `fontSize`, overflow) **first**
- then assign `onChange` / `savedAs` / `inputType` / `value`

Do it the other way round and the alignment survives until the first keystroke, then snaps
back to the donor's.

Related: the donor is a multi-line comment box anchored at its top, so a cloned field's text
rides the top edge. `rawPivot = Pivot.Left` puts it on the transform's vertical centre, which
is what a one-line field's box should be drawn around.

### Console return values do not reach `Player.log`

Quantum Console renders a command's return string in its own view and does not log it. A
probe that wants its answer in the log must `Debug.Log` as well as return — `pbj.ui-dump`
does, `pbj.ui-spike-status` did not, and its output was lost.

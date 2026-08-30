# Counter design notes

How the interface is built and why, in the detail somebody changing it would need: the accent
engine, the glass materials, the icon system, the geometry, the storage model, and the places
where the implementation deliberately departs from the brief it was built to.

This is the reference for contributors. For what the application is and how to install it, see
the [README](../README.md).

---

## Design system

Every visual value lives in `src/Counter.App/Theme/`. Views reference resources; they never
hardcode a colour, size, radius or margin, and a test walks the XAML to prove it.

| File | Holds |
| --- | --- |
| `Colors.xaml` | The two values that cannot be a brush: transparent, and the shadow colour |
| `Brushes.xaml` | Every brush in the app, declared once |
| `ThemePalette.cs` | The neutral glass sets, the accent derivation and the four state ramps |
| `ThemeService.cs` | Applies a theme and an accent by replacing brushes; nothing is rebuilt |
| `GlassNoise.cs` | The cached grain tile, generated once at start-up |
| `Typography.xaml` | Font stack, the type scale, and every `TextBlock` style |
| `Dimensions.xaml` | Radii, hairline thickness, the 4/6/8/10/12/16 spacing steps, control sizes |
| `Controls.xaml` | Every control template |

Colour is assembled from three independent inputs, and keeping them independent is the point.

1. **Neutral** - the surfaces, text and borders that make up almost all of the interface. Two
   hand-tuned sets, one per theme, carrying exactly the same keys.
2. **Accent** - everything that follows the colour the user picked. Not listed anywhere:
   *generated*, stop by stop, from one base colour, so choosing Orange cannot leave a blue focus
   ring behind in some corner nobody looked at. There is no orange branch and no blue branch.
3. **Semantic** - paused, warning, error and completed. Fixed families that do not follow the
   accent, because a paused timer that turns pink when the accent is pink has stopped carrying
   information. They run through the same generator, so a red ramp is lit exactly the way an
   accent ramp is; only the hue is fixed.

A test asserts the merged key set is identical across both themes and all six accents - twelve
combinations, one key set - because a key present in one and missing in another is a control
that goes black in light mode or invisible in dark mode, and that is the single most common way
a theme rots.

### Accent

**Six families ship, and each one is a name and a single colour.** That is the whole palette
definition:

| Palette | Base |
| --- | --- |
| Blue | `#438BFF` |
| Cyan | `#23BDD4` |
| Green | `#35C77D` |
| Purple | `#9468F2` |
| Pink | `#F15C9D` |
| Orange | `#FF9638` |

Everything else - seven ramp stops, the contour, the halo, the ambient reflection, the tint, the
hover and pressed tones, the four heat levels, the readable ink - is generated from that one
colour by `AccentEngine`. Only the identifier is stored. An unreadable value falls back to Blue
rather than throwing, because a bad colour preference is not a reason to refuse to draw an
interface.

**And a seventh swatch, which is any colour you like.** It is stored as `custom:#RRGGBB` and it
goes through the identical derivation, so a colour mixed by hand is not a second-class accent: it
gets the same five lit stops, the same contour, the same halo and the same measured ink that Blue
does. A custom colour that had to describe its own gradient would be exactly the hand-assembled
list this whole system exists to abolish.

The picker is three strips, and they are drawn in OKLCH because that is what the engine derives
in. This matters more than it sounds. An HSV picker drawn in HSV promises a smooth sweep and
delivers a band of neon and a band of mud, because HSV's "brightness" is not brightness; in OKLCH
a step along a strip is the same perceptual step wherever you take it. The gradient under the
thumb is therefore not an approximation of the result - it is the result.

| Strip | Moves | Range |
| --- | --- | --- |
| Colour | the hue | the full circle, joined at both ends |
| Intensity | the chroma | grey, to as vivid as sRGB can show at that hue |
| Brightness | the lightness | exactly the band the engine accepts, and no wider |

Two consequences of that last column: every reachable position is a legal accent, so nothing is
silently clamped on the way in, and colour past what the display can show is mapped back down by
giving up chroma alone - so the hue you picked is the hue you get.

The two coordinates a strip is not responsible for are inputs to it, so the colour strip restates
itself at the brightness you chose and the brightness strip restates itself in your hue. Nothing
ever shows you a colour you cannot have.

Beside them is the same colour as text, both ways. Anything a person would reasonably write is
accepted - `red`, `#E5484D`, `E5484D`, `#7B4` - and anything that is not a colour is simply not
applied, because a half-typed colour is what a text field being typed into looks like and it is
not an error. The field snaps back to what the interface is actually wearing.

The interface follows the thumb while you drag, but only the release is written down: a drag is
one decision expressed as several hundred mouse-moves, and only the decision belongs in the
database. The mixed colour is remembered separately from the accent, so trying Green for an
afternoon does not throw away the colour you mixed before it.

#### How a ramp is derived

In OKLCH, not RGB. Lightening a colour by pushing its channels toward 255 desaturates blue into
lavender and turns orange into cream, because the channels do not carry equal perceptual weight
and the hue drifts the moment one of them saturates. In OKLab the axes are close to independent,
so "the same colour, lighter" is one number and it stays the same colour - which is the entire
reason a single base colour can produce a ramp that still looks like one material.

The shape is measured rather than invented. The six reference palettes and the three state
families were analysed in OKLCH, and two patterns fell out: the **lit** stops converge on an
absolute lightness, around 0.91 and 0.86, because a highlight is the light source rather than the
material; the **shadowed** stops sit a fixed distance below the base, because a shadow is the
material with less light on it. Fitting those constants against all nine reference ramps leaves a
mean error of about 0.03 in OKLab, comfortably under a just-noticeable difference.

| Stop | Lightness | Chroma |
| --- | --- | --- |
| Highlight | 0.912, or base + 0.06 if the base is already paler | base x 0.45, capped at 0.070 |
| Light | 0.862, or base + 0.035 | base x 0.80, capped at 0.095 |
| Base | the chosen colour | the chosen colour |
| Strong | base - 0.070 | base x 1.06 |
| Deep | base - 0.165 | base x 1.00 |
| Shadow | base - 0.315 | base x 0.82 |
| Glow | base + 0.060 | base x 1.12 |

Chroma falls away at the lit end because sRGB cannot hold much of it up there and a highlight
that stays saturated reads as a second colour rather than as light, and swells slightly just past
the base, which is where a real material looks richest. Anything that lands outside sRGB is
gamut-mapped by giving up chroma and **only** chroma - bisected down to the largest value the
display can reproduce - so the lightness and the hue, the two things the derivation meant,
survive untouched. The hue is preserved to within two degrees at every stop: a test asserts it
across the six families and eight awkward colours including white, black, pure grey and the most
saturated lime sRGB has.

The lightness sequence is guaranteed strictly descending, with the shadowed stops held at least
0.014 apart, so even a nearly black base produces a gradient rather than five copies of one
colour. A base too pale or too dark to fit a ramp around is brought to the nearest edge of the
usable band rather than refused. Four thousand random colours were run through it: none produced
a non-monotonic ramp, and the largest hue step between adjacent stops was 25 degrees.

#### Ink

Two inks, because a word and a shape do not need the same contrast.

- **`AccentForegroundBrush`** is text. Measured against the base *and* the light stop, taking
  whichever of white and `#111318` reads better. On every one of the six families that is the
  dark ink, because white on a saturated mid-tone does not reach 4.5:1 - and a label crossing
  into the lit half of the ramp is exactly the case the rule exists for.
- **`AccentGlyphBrush`** is a filled icon. WCAG asks 3:1 of a graphical object rather than 4.5:1,
  and a play triangle at 17 pixels is one, so white is taken whenever white genuinely clears that
  bar across the base-to-strong region a centred glyph covers. That gives the conventional white
  play button on blue, purple and pink, and dark ink on cyan, green, orange and any pale colour
  somebody picks, where white would be a smear.

Neither is a choice. Both are measurements, and both are asserted for every family and every
awkward colour.

#### Where it goes

The accent drives the main tool contour, the primary play control, the running progress light,
the active task indicator, the selected calendar date, the selected accent swatch, the keyboard
focus ring, the statistics bars, the heat ramp, and the soft reflection the glass picks up near an
active session. It never touches a destructive, paused, warning, error or completion state.

Glass surfaces, task rows, inputs, body text, heatmap cells and tooltips stay neutral. That is
what keeps the chosen colour meaningful: an interface where everything is orange does not tell you
anything by being orange.

Which end of a ramp reads as "the accent" depends on what it is drawn against. On a dark surface
the base is the right weight; on white it is too pale to be legible as text or as a hairline, so
the light theme takes the strong stop. Hover and pressed tones are the ink moved a third of the
way toward the lit and shadowed stops, and the heat ramp is the ink mixed into the empty-cell
surface in four even steps.

### Semantic states

Fixed, and independent of the accent.

| State | Base |
| --- | --- |
| Paused | `#F3B643` |
| Warning | `#E6AA4F` |
| Error and destructive | `#EF6472` |
| Completed | `#39C781` |

One colour each, run through the same engine as an accent, so a red ramp is lit the same way a
blue one is. Only the tone picked off each ramp is theme-aware: a light interface takes the strong
stop so the colour stays legible on white. Idle is the selected accent at reduced strength - the
tool is never without a contour, but nothing glows when nothing is happening.

Completing a task is a meaning rather than a preference, so the tick and the wash behind a
finished row are the completion family whatever the accent is. If the tick followed the accent,
choosing green would make every open task look done and choosing red would make finishing one
look like a failure.

**State is never colour alone.** Running shows a Play or Pause glyph and a moving track; paused
shows the Pause glyph and a held track; completed shows a Checkmark and a static one. If you
choose Green as your accent, ongoing and completed share a colour family and are still told
apart by icon, by whether the progress is moving, by the label and by which control they are on.

### Where a gradient is allowed

Only on things that are genuinely active: the primary play control, the running timer track, the
live notch edge, the selected accent swatch, the selected calendar date, the statistics bars, and
the paused, warning, error and completion indicators.

Never on panel backgrounds, task rows, ordinary inputs, normal borders, secondary buttons, body
text, the settings background, heatmap cells, tooltips, icons or badges.

Each one is **five stops of a single family** at fixed offsets - Highlight at 0, Light at 18,
Base at 48, Strong at 76, Deep at 100 - running from the upper left to the lower right, which on
a square control is the 135-degree direction the design calls for.

Five rather than three because three cannot describe light: a three-stop ramp reads as a blend
between two colours with something in the middle, where five gives the lit edge somewhere to go
before the material's own colour arrives and gives the shadowed half room to deepen without the
middle looking washed out. The offsets are not evenly spaced, for the same reason the middle stop
used to sit at 45 percent: the lit pair sit close together in the first fifth and the shadowed
half runs long, the way light falls across a curved surface. Every gradient in the application
uses these five offsets and this one direction, and a test asserts it - two adjacent controls lit
from opposite corners is what makes an interface look assembled rather than lit.

**Light is separate from colour.** The white overlays are their own brushes and are never baked
into a colour ramp, because brightening every stop of a gradient to fake a highlight is what makes
a gradient look bleached. There are three, they are white in both themes and under every accent,
and a test asserts they never pick up a hue:

| Brush | What it is |
| --- | --- |
| `GlossOverlayBrush` | The glossy reflection down the face of a primary control. Straight down rather than diagonal, because a reflection on a convex surface follows the surface |
| `TopHighlightBrush` | The thin sheen catching the top edge of a small control. Never above 16 percent, gone before the middle |
| `SpecularHighlightBrush` | A localised reflection for the larger surfaces, thrown from 22 percent across and 10 percent down - the same origin as every other highlight |

Active primary controls also carry a halo of the palette's own glow at no more than 12 percent.
It is a radial fade rather than a blurred drop shadow: the visual result is the same at that size
and it costs one gradient fill instead of a render-target blur pass on every frame the panel is
resizing.

### Liquid glass

Every top-level surface - the notch, the planner, statistics, settings, every popover and the
undo snackbar - is a `LiquidGlassPanel`, and it is a control rather than a pattern because glass
is not one property. A convincing pane needs about eight layers stacked in a specific order, and
any view assembling them by hand will eventually get one wrong: a reflection under the tint, an
inner edge outside the clip, grain over the content, a second shadow on a surface that already
had one. Written once, the whole interface is made of the same material.

```
halo        light thrown outward instead of shadow           liquid only
glow        the accent, at a tenth or less, spilling just past the edge
contour     a real one-physical-pixel ring, lit from the upper left
body        the glass itself, clipped so nothing inside can reach a corner
  tint          the wash that gives the surface its own colour
  reflection    the warm light of something active nearby, when there is something
  edge light    the far corner catching light, gone by the middle     frosted only
  top sheen     the fall of light down the face                       frosted only
  specular      the highlight where the light actually lands
  ripple        the slow unevenness of a pour                         liquid only
  inner edge    a fainter second line, which is what reads as thickness
  rim           a blurred stroke inside the clip: light held in the edge
  grain         two percent of monochrome noise, so the surface is not flat
  content
accent ring the coloured contour, laid exactly over the structural one
```

A test asserts that order.

The **grain** is one 128-pixel tile, generated once from a fixed seed, frozen, and tiled in
absolute units so it stays fixed relative to the panel instead of stretching when the panel
resizes. Monochrome, so it tints nothing; two percent, so a full black-to-white spread moves the
underlying colour by about one level. It is felt rather than seen, and it is never animated.

The **contour** is built as nested borders rather than as a stroked border: the outer border is
filled with the contour brush and padded by exactly one physical pixel, and the glass body sits
inside that padding. That is what keeps the ring the same weight all the way round a rounded
corner, which a stroke does not. The accent ring is laid exactly over the structural one and only
its opacity changes, so a state crossfade is one animation and the tool never loses its edge
halfway through.

| State | Accent contour | Glow |
| --- | --- | --- |
| Idle | 40% | 0 |
| Hovered or expanded | 58 to 62% | 4 to 5% |
| Running | 95% | 10% |
| Paused | 92%, amber | 9% |
| Final minute | 100%, red | 12% |
| Completed | 95%, green | 10% |

Every top-level card takes the same ring at the same strength at the same moment. A planner
outlined more faintly than the notch it is attached to would read as two objects.

#### Three glasses

The material is a fourth independent input, alongside the theme, the accent and the display.

| Material | What it is |
| --- | --- |
| Solid | dense smoked glass, a drawn edge, one specular highlight |
| Frosted | a pale wash, a soft rim instead of a hard second line, edge light and a top sheen |
| Liquid | almost no tint, one wide soft rim, a ripple, and a halo outside |

They share one layer stack and differ only in which layers are lit, because three templates would
be three places for the layer order to drift apart.

#### Where the blur comes from

The notch is a **layered window**. That is what gives it genuinely rounded corners, a flush top
edge and a frame that passes clicks through, and it is also the reason nothing drawn inside it can
blur what is behind it: a layered window is composited by `UpdateLayeredWindow` and never goes
through DWM, so there is no backdrop to sample. Frosted glass without a blur is not frosted glass,
it is a thinner sheet of the same glass.

So the blur is put where a blur can exist. Each glass surface gets a small, ordinary, non-layered
window sitting **directly beneath it** in the z-order, carrying the compositor's acrylic and
rounded to sit inside the outline of the panel above it. The notch window is not touched at all -
not its transparency, not its hit testing, not its geometry - and the panel keeps drawing its tint,
its rim and its ripple on top. What changes is only what those layers are drawn over.

```
[ backdrop window ]   ordinary, non-layered, acrylic, rounded by DWM
        beneath
[ notch window    ]   layered, unchanged
```

Those windows cannot be clicked, cannot be focused, do not appear in the task switcher, and exist
only while a translucent material is chosen. Choosing Solid destroys them rather than hiding them.

Five details that took measuring rather than guessing:

- **`SetWindowRgn` does not clip an acrylic blur.** It was assumed here that it did, and the
  assumption held for as long as nobody had a blur to look at: with Windows transparency effects
  turned off the compositor returns a flat colour and a flat colour has no corners. Turn the
  setting on and a square corner of blur appears past every curve. Verified on build 26200 by
  clipping a live backdrop to half its width and watching precisely nothing change.
- **On Windows 11, DWM's own corner preference is the only thing that will.** `DWMWCP_ROUND`
  clips the blur, at DWM's fixed eight-pixel radius rather than at the panel's. Windows 10 has no
  corner preference and there the region does work, so the two are not a preference and a
  fallback - they are two different operating systems, and the window uses whichever it is on.
- **Which means the backdrop is drawn slightly inside the panel.** Two curves of different radii
  laid over each other cross; nesting them is arithmetic. Circles nest when the distance between
  their centres is at most the difference of their radii, the centres here sit on the diagonal, so
  an inset of `(R - 8) x (1 - 1/root 2)` - about twenty-nine percent of the gap - is the least
  that works. The top edge is the exception: the notch meets the bezel square, so when a panel is
  already against the top of its monitor the backdrop is extended *above* the screen and DWM
  rounds it where there is nothing to see.
- **Rounding brings a shadow with it, and it is not optional.** DWM shadows a rounded window, so
  the panel above gives up its own while a backdrop is under it (`LiquidGlassPanel.HasBackdrop`).
  Two shadows around one edge is the dark halo that makes a translucent panel look like it is
  leaking rather than floating.
- **The acrylic tint cannot be near zero.** Below roughly six percent alpha the compositor stops
  producing a blur and hands back flat black. A tint of `0x10` looked like a triumph - panels that
  hid a wall of coloured text completely - and was in fact an opaque rectangle.

#### Two density tables

How thick the glass is depends on whether a blur is actually behind it, and it has to. A frosted
card on a web page sits at eighteen percent because `backdrop-filter` has already destroyed the
detail behind it; the tint is only tinting a wash. Take the blur away and the same alpha leaves a
browser with readable text in it sitting under the timer.

| Panel body | With a blur | Without one |
| --- | --- | --- |
| Solid | 87 percent | 87 percent |
| Frosted | 52 percent | 81 percent |
| Liquid | 25 percent | 74 percent |

The app moves between the two tables live, so turning the blur off repaints the glass rather than
leaving it see-through. Two rules hold in both, and both are enforced by tests: each material lets
strictly more through than the one before it, and without a blur none goes far enough for a
sentence behind the glass to become legible. A translucent material is also given stronger ink -
quiet text over a translucent panel is not quiet, it is gone.

The blurred column is denser than the reference designs it is drawn from, and the reason is worth
writing down: **a blur destroys detail, it does not change luminance.** A white wallpaper blurred
is still white. A card on a web page can sit at eighteen percent because the page behind it is a
page somebody designed; a panel floating over whatever wallpaper a user happens to have is not
that, and the difference is the whole contrast section below.

#### Contrast

Every ink clears **4.5:1** against the surface it is drawn on, in both themes, on all three
materials, over any wallpaper - except the muted tone, which clears **3:1**, the threshold for
text that labels rather than states.

That cannot be computed. The acrylic blend is the compositor's, not ours, and what reaches the
glass is whatever happens to be behind the window. So it is photographed instead: the real window
over a field of forty-pixel bands of saturated colour, pure white and pure black among them,
sampled across the content area at every row, worst value kept. The worst surface each material
actually produces:

| | Solid | Frosted | Liquid |
| --- | --- | --- | --- |
| Dark | `#46494F` | `#5C5D61` | `#616267` |
| Light | `#E6E7E7` | `#DEE2D9` | `#CED5C4` |

and what the ink ladder gets on it:

| | primary | secondary | muted |
| --- | --- | --- | --- |
| Dark, solid | 8.40 | 4.96 | 3.32 |
| Dark, frosted | 6.12 | 4.67 | 3.24 |
| Dark, liquid | 5.66 | 4.79 | 3.17 |
| Light, solid | 14.77 | 5.65 | 3.56 |
| Light, frosted | 13.93 | 6.25 | 3.46 |
| Light, liquid | 12.14 | 6.99 | 4.12 |

Getting there cost the quiet end of the ink ladder some of its quiet and the glass some of its
transparency, in that order, because ink is cheaper than opacity: compressing a ladder loses
hierarchy, and thickening glass loses the material. The numbers above are pinned by a test, so an
ink cannot be quietened back without the failure being named. If the densities move, the surfaces
move with them, and the way to find the new ones is to take the photograph again.

#### When Windows will not blur

**Transparency effects** in Personalisation, Colours is a global switch, and with it off DWM blurs
nothing for anybody: it substitutes a solid colour, and every acrylic surface on the machine goes
opaque. A browser is not affected, because CSS `backdrop-filter` is the browser's own compositing
rather than the system's - which is exactly why a reference card can look blurred on a machine
where nothing native does.

The app reads the setting rather than assuming, on every repaint, so flipping the switch changes
what is on screen rather than what is on screen after a restart. When it is off, the glass falls
back to the density that is legible without a blur and the settings panel says why in one line, so
a panel obeying a preference does not read as a panel that is broken.

#### The layers themselves

The **rim** is how an inset glow is actually built: a blurred stroke drawn inside a clipped body,
so the clip cuts away the outer half of the blur and the inner half becomes light held in the edge.
The **ripple** stands in for the reference's displacement filter, which needs to sample the
backdrop through a fractal noise field; what is reproduced is the noise field itself, laid over the
glass as variation in how much light it holds. Same texture, different job: not light bent by an
uneven surface, but an uneven surface catching more light in some places than others. It tiles
seamlessly, is generated once, and never moves.

The structural contour survives all three materials. The reference designs for the two translucent
ones carry no border at all and let the inset glow do the work, and on a web page that is right:
the card sits inside a document that already frames it. This panel sits on top of whatever the
desktop happens to be, and an edge that disappears over a pale wallpaper is a window you cannot
find.

### One physical pixel

A WPF logical pixel is a physical pixel only at 100 percent. At 150 percent a one-unit border is
one and a half device pixels, which the rasteriser resolves as a two-pixel line at three-quarter
strength: soft, uneven, and visibly heavier on some edges of a rounded rectangle than others.
Since the contour is the most structural line in the design, that is not left to rounding.

`DpiService` puts `1 / DpiScale` into the resource dictionary as `HairlineThickness` and
`HairlinePixel`, and recomputes them on `WM_DPICHANGED`. Every hairline in the application - the
outer contour, the inner edge, dividers, the progress track, the checkbox stroke, popover and
focus rings - resolves them **dynamically**, so one call moves all of them together and a test
fails the build if any of them resolves statically. Rendering tests count the actual device
pixels at 100, 125, 150 and 200 percent.

### The backdrop

`BackdropService` asks the compositor for a real blur and reports, honestly, that it cannot have
one.

Windows 11 can blur what is behind a window for free through `DWMWA_SYSTEMBACKDROP_TYPE`, and it
is much better than anything an application can paint. It also has a hard prerequisite: the window
must not be layered. Counter is layered - it is a transparent frame with a small rounded card
floating in it, which is what lets the notch have real rounded corners, sit flush against the top
bezel and pass clicks straight through everywhere else. Trading that away for a blur would change
the window's transparency, its hit testing and its geometry.

The alternatives are worse rather than better. `SetWindowCompositionAttribute` with acrylic does
apply to a layered window, but it blurs the whole window rectangle: on a window that is mostly
transparent frame that paints a large blurred rectangle behind nothing, with square corners.
Capturing and blurring the desktop by hand would mean re-reading the screen on a timer.

So the service probes rather than forces: if the window is ever not layered on a build new enough
to carry the attribute it asks for the backdrop and reports `Native`, and today it reports
`Simulated` with the reason, which is written into the diagnostics log at start-up. The layered
glass does the work - the same layers, the same layout, the same contour, just without a
compositor blur behind them. Nothing about the interface moves between the two modes.

One consequence is deliberate and visible. The design's opacity targets - 80 to 86 percent for a
panel - assume a blur, which destroys the detail behind the glass and lets it be that
transparent. Without one, what shows through is the desktop at full sharpness, and at 80 percent
a line of text underneath the panel is not a suggestion of depth, it is a line of text you can
read. The assembled panel therefore lands near 90 percent: enough that the desktop is a tone
rather than content, little enough that the surface still moves with what is behind it.

The halo is a radial fade rather than a blurred drop shadow. The visual result at that size is
the same, and it costs one gradient fill instead of a render-target blur pass on every frame the
panel is resizing.

Two tests hold the line: gradients may only be declared in `Brushes.xaml`, and every gradient's
three stops must get monotonically darker and stay within 40 degrees of hue of each other. The
old palette - unrelated pink, purple, blue and cyan stops blended together in three different
places - would fail both.

**Dark neutrals**

| Role | Colour |
| --- | --- |
| Window background | `#0E1116` |
| Primary surface (cards) | `#141820` |
| Raised surface (rows, inputs) | `#1A202A` |
| Hover | `#202735` |
| Pressed | `#252E3C` |
| Subtle border | `#2A3340` |
| Visible border | `#354152` |
| Text: primary / secondary / muted | `#F4F7FA` / `#AAB4C2` / `#737F90` |

It is deliberately not pure black. A test asserts the five surface steps climb monotonically and
that the whole run spans enough tone to read as depth rather than as five near-identical blacks.

**Light neutrals**

| Role | Colour |
| --- | --- |
| Window background | `#F1F4F8` |
| Primary surface (cards) | `#FFFFFF` |
| Raised surface | `#F7F9FC` |
| Hover / pressed | `#EEF2F7` / `#E5EAF1` |
| Subtle / visible border | `#D8E0E9` / `#C7D1DD` |
| Text: primary / secondary / muted | `#11151C` / `#566273` / `#8792A2` |

Light mode uses the stronger border tone on the outer edge so the card is findable against a
white wallpaper, and a soft neutral shadow rather than black, which on a light ground reads as
dirt. Tests assert primary text clears 7:1 against the card and secondary text clears 4.5:1.

**Theme selection** is `System`, `Light` or `Dark`, offered in the tray and in Settings, and
remembered between runs. `System` is the first-run default and follows the Windows app theme,
including when it changes while the app is running. Theme and accent are independent: switching
from Dark to Light keeps the accent, and switching from Blue to Orange keeps the theme.

Switching is not a rebuild. Every brush reference in the app is a `DynamicResource`, so applying
a theme or an accent replaces the entry behind each key and every reference re-resolves on the
next layout pass: no dictionary is swapped, no template is regenerated, no window is recreated,
the panel keeps whatever state it was in and the timer keeps running. The replacements are
frozen, so rendering afterwards costs no more than before. The three drawn controls declare the
brushes they paint with as dependency properties, because a control that draws itself keeps its
drawing until something invalidates it and a resource changing underneath it is not something it
would notice.

**Edges carry state:** a neutral hairline at rest, the accent gradient while running, amber while
paused, red in the final minute, green on completion. The glow behind the active notch edge is a
single flat colour at no more than a tenth opacity with a fixed blur radius - flat because it is
about to be blurred, and a gradient inside a blur is work nobody can see. The radius is never
animated, and the blur is detached entirely while the panel is changing size, because it is the
one effect here expensive enough to cost a frame.

### Icons

One family: **Microsoft Fluent UI System Icons**, MIT licensed, release `1.1.339`. The 47 SVG
files actually used are bundled verbatim under `Assets/Icons/Fluent/` beside a `manifest.json`
recording the source, the revision, the commit and a SHA-256 for each file. No other icon set, no
symbol font, no emoji, no raster image and no hand-drawn approximation appears anywhere.

`tools/Sync-FluentIcons.ps1` is the only thing that puts an icon into the application. It reads
the list in `tools/icons.psd1`, downloads exactly those files from the pinned tag, checks each
one against the manifest, verifies that every file is single-colour filled artwork on a square
viewBox with no stroke and no transform, and generates `Controls/IconCatalog.g.cs`: the
`IconKind` enum, the lookup table and the path data itself. It also parses every geometry as it
goes, so artwork WPF cannot read fails the conversion rather than the application.

The build never runs it. The generated file is committed, so a build has no network dependency
and the application has none at runtime. `Sync-FluentIcons.ps1 -Verify` re-checks the bundled
files against the manifest without touching the network at all.

`AppIcon` draws one geometry, and it is the only thing in the application that draws an icon.
It is a drawn element rather than a `Viewbox` around a `Path`, for three reasons in order of how
much they matter:

1. **One centring rule.** The element measures to an exact square of `IconSize` whatever the
   artwork inside it looks like, and the geometry is scaled from its own source viewBox and
   centred in that square. A 12 x 12 checkmark and a 20 x 20 chevron therefore sit on the same
   optical centre with no per-view margin anywhere.
2. **Aspect ratio cannot be lost.** One uniform scale factor, and no `Stretch` to set to `Fill`
   by accident.
3. **Cost.** One geometry and one drawing instruction per icon, against a `Viewbox`, a `Canvas`,
   a `Path` and a full layout pass each. The notch draws around thirty of them.

| Rule | Value |
| --- | --- |
| Source viewBox | 20 x 20, preserved rather than normalised (the checkmark is 12 x 12) |
| Normal icon | 16 x 16 |
| Metadata icon | 12 x 12 |
| Play glyph | 14 x 14 |
| Normal hit target | 28 x 28 |
| Compact hit target | 24 x 24 |

Optical corrections live in one table in `IconCatalog`, never in a view, and are capped at one
pixel - a larger number means the icon is wrong, not that it is off-centre. There is exactly one
entry, and it was decided by measurement: every bundled geometry's ink bounds were compared with
its own viewBox centre, and all of them land within a quarter of a unit of it except the pin and
the play triangle. The triangle gets half a pixel to the right, because a triangle's optical
centre is its centroid at `(5+5+18)/3 = 9.33` rather than the middle of the box around it. A
render test asserts that with the correction applied its centre of mass lands within a device
pixel of the button's centre at 100, 125, 150 and 200 percent.

Filled variants are used only for primary actions and active states: play, pause, stop, the
streak flame, the pin when it is pinned, and a selected destination in the header.

### Icon buttons and badges

`IconButton` and `IconToggleButton` carry a typed `IconKind` rather than a geometry stuffed into
`Tag`. A `Tag` holds anything, so a view could put a brush or the wrong geometry in one and get a
silently empty button; a generated enum cannot compile if the icon is not in the family.

One template implements every state, so no two icon buttons anywhere can disagree about what
pressed looks like: transparent and secondary ink at rest, raised surface and primary ink on
hover, pressed surface with the ink moved half a pixel down by a transform rather than a margin,
accent-tinted with an accent icon when it is the current destination, 38 percent and no hover
when disabled. The keyboard ring is a WPF focus visual rather than a trigger on
`IsKeyboardFocused`, which is what makes it appear after Tab and stay away after an ordinary
mouse click without any template having to guess which happened.

`CircularBadge` is one circle with one thing centred in it - a calendar date, a count, a status
dot. Every off-centre badge in the old interface was off-centre for the same reason: the circle
and its content were siblings with padding, so the moment the content changed width the circle
moved and a two-digit date sat differently from a one-digit one. Here the size comes only from
`Diameter`, the disc is drawn by the control itself rather than placed in its template, and the
content is centred in the same box. Text width cannot reach the circle. The one permitted
correction is half a pixel of baseline, applied to the content and never to the circle, because
digits sit optically low inside a circle. Render tests measure the disc at 100, 125, 150 and 200
percent for 1, 8, 11, 20, 28 and 31: same size, same position, every time.

`CompletionCheck` is the completed-task control, and a dedicated control rather than a styled
`CheckBox`. The old one drew a ring, a separate focus ring and a tick as three independent
siblings, which is exactly how a checkmark ends up a pixel off centre and a blue outline ends up
doubled. There is now one 16 x 16 visual inside a transparent 28 x 28 hit target:

- **Unchecked**: a 1.25-pixel neutral circle. No fill, no glow, no second outline.
- **Hovered**: an accent border and a very faint accent-tinted fill.
- **Checked**: the accent's own base colour as both fill and contour, so there is exactly one
  contour and it cannot be a different weight or hue from the thing it surrounds. A white Fluent
  checkmark at 10 px, clipped to the circle.
- **Focused**: a 20-pixel ring outside the 16-pixel circle, 2 pixels clear of it, shown after a
  Tab and not after a click.

The fill crosses in over 100 ms and the tick over 120. Nothing scales, bounces or spins, and
every element is a fixed size, so ticking a task off cannot move a pixel of the row it is in - a
test measures the control checked and unchecked and asserts the two are identical.

**Type.** Segoe UI Variable with a Segoe UI fallback. Body 12, rows 11.5, secondary 10.5,
metadata 10, pills 9.5, section labels 11.5 semibold, timer 12 semibold with tabular numerals so
the countdown never nudges the layout, and calendar dates in tabular numerals so a badge never
has to move for a wider digit.

## The heatmap is a drawn control

`JourneyHeatmapControl` renders the twelve-by-seven grid itself rather than hosting eighty-four
elements in a stretched `UniformGrid`.

That is what makes it crisp. Every square edge is snapped to the device pixel grid through a
`GuidelineSet`, so at 125 and 150 percent the edges land on whole pixels instead of straddling
them and going soft. It also means a refresh is one render pass rather than eighty-four layout
passes, and the control's own size never depends on the data, so nothing it does can resize the
panel around it.

- Twelve weeks including the current one, seven rows, Monday at the top, Sunday at the bottom.
- Nine-pixel squares with a three-pixel gap and a two-pixel radius in the quick view; eleven-pixel
  squares with month labels and a legend in the statistics panel. Same control, same data.
- Today carries a subtle outline. Future days are drawn as the empty level and say only their
  date, because there is nothing to report about them yet.
- Hover and keyboard focus both describe a day; arrow keys move between squares; every square
  carries an accessible one-line description.
- It re-renders when the activity, the theme, the date, the DPI or its own size changes, and at
  no other time. A running timer never touches it.

"Crisp at every DPI" is a claim about pixels, so it is tested as one: the control is rendered to
a bitmap at 96, 120, 144 and 192 DPI and the straight edges of the squares are inspected. A
scanline across them must hold exactly two distinct values - inside a square, or not - and every
square must come out the same width in whole device pixels. The corners are excluded, because a
two-pixel radius is a curve and a curve is anti-aliased on purpose.

## How the timer stays correct

The remaining time of a running session is **never** a counter decremented once per second. It is
always derived from an absolute UTC target instant:

```
target    = currentRunStartedAtUtc + (plannedSeconds - elapsedSeconds)
remaining = max(0, target - utcNow)
```

The dispatcher tick (500 ms) exists only to repaint. Consequences:

- Sleep, hibernation, screen lock and clock jumps are absorbed automatically.
- **Pause** stores the exact remaining seconds; **Resume** builds a fresh target from that
  remainder, so pausing repeatedly does not accumulate drift.
- State is written to SQLite on every start, pause, resume, cancel and completion.
- On launch, a surviving session is re-attached. If its target passed while the app was closed,
  it is completed at its true target instant, not at launch time, exactly the planned time is
  credited, the completion is recorded once, and the interface says
  `Completed while Counter was closed` on a strip that can be dismissed.
- Only one session can be active at a time; `FocusEngine.Start` refuses a second one.
- Completion fires exactly once, no matter how often the engine is polled.

### One authority for the session

`FocusSessionService` is the only thing that decides what a play press means and the only thing
that writes a session. The quick view, the planner, the collapsed notch transport, the tray
command and the global shortcut all call it, so they cannot disagree.

| Condition | Glyph | A press does |
| --- | --- | --- |
| No active session | play | start this task |
| This task is running | pause | pause it |
| This task is paused | play | resume it |
| Another task is active | play | ask before interrupting |
| The duration is not usable | play | open the duration picker |
| A press is already committing | disabled | nothing |

The glyph is not guessed locally: the row asks the service what a press *would* do, so what you
see and what you get are derived from the same state.

- Every transition is snapshot, apply in memory, persist, and on a failed write put the engine
  back exactly as it was - so the interface can never show a running timer for a session that was
  never saved. The failure surfaces as a non-blocking banner.
- A switch cancels the old session and starts the new one in a **single transaction**, so the
  database is never caught holding two live sessions, or none when you asked for one. The old
  session keeps the time it actually accumulated.
- A second press on the same task within the double-click window is treated as a stutter and
  ignored, so double-clicking play cannot start a session and instantly pause it. Disabling the
  button visually is not enough on its own: by then the second press is already queued.
- At startup, a database that somehow holds more than one live session is repaired: the newest is
  kept and the others are cancelled with the time they had accumulated. Nothing is ever deleted,
  and the repair is logged.
- A completion is announced only after it has been committed, so anything that reads storage in
  response - the journey surface above all - cannot race ahead of the row it is looking for.

## How the journey streak stays correct

The streak is **derived** from what is stored, never kept as a counter, so it cannot fall out of
sync with the data. A local calendar day is productive when it carries at least one
**contribution**, and a contribution is one of three things:

1. a completed task attributed to that date,
2. a successfully completed focus session attributed to that date, or
3. a positive manual time entry on that date.

They are counted separately on purpose: finishing a task and finishing a session are different
pieces of work and both deserve to count.

An unfinished task is not productivity. Cancelled, running and paused sessions never count. Two
contributions on one day raise the square's intensity but still count as a single streak day, and
a date in the future is stored and shown but cannot extend a streak that ends today.

The date a contribution counts for is **stored**, in `Task.CompletedForDate` and
`FocusSession.CompletedForDate`, rather than derived at read time from an instant:

| Action | Contribution date |
| --- | --- |
| Complete a task scheduled for a past day | that scheduled day |
| Complete an unscheduled task | today |
| Save a task with **Already completed** ticked | the day selected in the calendar |
| Move a completed task to another date | the new date |
| Mark a task incomplete, or delete it | none: the contribution is gone |
| Finish a focus session | the local day the countdown actually reached zero |
| Add time by hand | the day chosen in the Add time form |

Storing the day rather than converting an instant is what makes ticking off yesterday's task light
up yesterday, and it is what stops a timezone change from moving contributions that were already
earned.

`JourneyActivityService` is the single source of that data. Every committed change that can affect
activity publishes one snapshot, and quick view and planner both render that same snapshot, so
they can never show different numbers. A refresh runs the query on a background thread over its
own read-only SQLite connection and lands on the dispatcher, typically within about 50 ms, without
closing or reopening the panel. Handing the drawn heatmap a new list is a single render pass that
cannot change the control's size, so one changed day repaints without disturbing anything.

Time actually run is reported per day too, split at local midnight, so a session from 23:30 to
00:30 puts half an hour on each day rather than an hour on whichever end happened to win. That
time appears in the tooltip and in the chart even when the session never reached zero, because it
was genuinely worked; it is just not a contribution.

### Deleted tasks

A deletion stamps `DeletedAtUtc` rather than removing the row, so the history attached to a task
survives it. Two consequences pull in opposite directions, and both are deliberate:

- **A deleted task is never listed.** It is gone from the top-tasks list, it is not counted in
  the per-task average, and it cannot be the day the most tasks were completed. A list of what
  you have been working on should not name something you removed, and a row reading "Deleted
  task" is worse than no row at all.
- **Its hours stay in the totals.** The time was spent whatever happened to the task afterwards.
  Subtracting it would also make the activity chart disagree with the journey heatmap, which
  counts the same hours from the same rows and knows nothing about deletion.

An id with no task row at all is treated as gone rather than as unknown. Sessions and manual
entries keep a copy of the title so history survives, but reaching for those copies here would
resurrect a name that was deliberately removed.

## Storage

| What | Where |
| --- | --- |
| Database | `%LocalAppData%\Counter\counter.db` |
| Logs | `%LocalAppData%\Counter\logs\` |

Directories are created on demand. The schema is versioned through SQLite's `user_version` and
migrations run inside a single transaction. Every statement is parameterised and foreign keys are
enforced.

**An existing database is never erased or recreated because of a migration problem.** A failed
migration rolls back and surfaces a readable message; a file written by a newer version of the app
is refused rather than downgraded.

Calendar dates (a task's `ScheduledDate`, and both `CompletedForDate` columns) are stored as plain
`yyyy-MM-dd` text with no instant attached, so changing the machine timezone can never move a task
or a contribution to a different day. Instants are stored as ISO-8601 UTC.

**Schema 2** adds `Tasks.CompletedForDate` and `FocusSessions.CompletedForDate`. Both are nullable
and added with `ALTER TABLE`, so every existing row survives untouched, and the migration backfills
in the same transaction: a completed task takes its scheduled day when it has one and otherwise the
local day it was completed on, and a completed session takes the local day of its completion
instant. The backfill runs in C# rather than SQL because a correct default needs the machine's real
local timezone. Rows that have neither a scheduled day nor a completion instant are left null and
simply do not contribute.

**Schema 3** adds time tracking, recorded work, soft deletion and end reasons. Every change is
additive - three nullable or defaulted columns and two new tables - so a file an earlier version
wrote keeps every byte of its content.

| Added | Why |
| --- | --- |
| `Tasks.DeletedAtUtc` | Deleting stamps this instead of removing the row, so the hours spent on a task survive the task |
| `FocusSessions.TaskTitle` | History still reads correctly after a rename or a delete |
| `FocusSessions.EndReason` | Completed, StoppedByUser, TaskCompleted, SwitchedTask, TaskDeleted, RepairedDuplicate |
| `FocusSegments` | One row per uninterrupted stretch of a session running |
| `ManualTimeEntries` | Work recorded without a timer, kept apart so it can never be double-counted |

Durations needed no column change at all: SQLite `INTEGER` is already 64-bit, so raising the
supported range to 99:59:59 only meant the code that reads them stopping narrowing to 32 bits.

The migration reconstructs one run per historical session that recorded time, laid down from the
session's own start for exactly the number of seconds that were stored. That is the most faithful
reading the old data supports, it can never invent time that was not already there, and without
it somebody's existing hours would silently become zero. A session that was still live gets an
open run; running it twice adds nothing, because every statement only fills what is null or
inserts what does not exist.

### Reliability

`PRAGMA foreign_keys=ON`, `journal_mode=WAL`, `busy_timeout=5000`, `synchronous=NORMAL`. Every
multi-row state change goes through one transaction.

- **An integrity check runs at startup.** A file that reports damage is left exactly as it is and
  the problem is shown, because a damaged file that can still be partly read is worth far more to
  its owner than a clean empty one.
- **A rotating local backup** is taken at most once a day, through SQLite's own backup API so it
  is consistent while the connection stays open, into `%LocalAppData%\Counter\backups\`. The
  seven most recent are kept. A failure is logged and swallowed: not having a backup is a
  disappointment, never a reason not to start.
- **Startup repairs what a crash left behind.** More than one live session keeps the newest and
  ends the rest with the time they had accumulated; a run left open is closed at its session's own
  end, capped at what the session was planned to take, never at "now". Nothing is ever deleted.
- **An unsaved task draft survives.** It is written after a pause in typing rather than on every
  keystroke, restored on the next launch, and cleared the moment the draft is saved or abandoned -
  a recovery prompt for something already dealt with is worse than no recovery at all.

### What is remembered

Tasks, notes, scheduled dates, completion state and contribution dates, planned durations, focus
sessions, focus runs, manual time entries, running or paused timer state with its absolute target,
journey history, theme, glass material, accent, the last hand-mixed colour, monitor, hover,
sound, start-with-Windows and stop-on-completion preferences, the last selected calendar day, the
last task filter, the last statistics range, and the unsaved task editor.

`--demo` inserts a few example tasks, and only into a database with no tasks at all.

## Window behaviour

One borderless transparent window. Its position and size go through `SetWindowPos` in **physical
pixels**, so mixed-DPI multi-monitor setups land exactly on the pixel grid.

**The window never changes width.** It is always as wide as the widest card can be, plus 16 px of
gutter on each side for the shadow, and it is fixed at the horizontal centre of the monitor. Only
its height animates, and there is **no** surplus at the top, so the card meets the screen edge
exactly. Everything outside the card is transparent, is not hit-testable, and passes both clicks
and hover straight through to whatever is underneath.

```
windowWidth = widestCard + 2 x gutter        (constant)
x           = monitorLeft + (monitorWidth - windowWidth) / 2
y           = monitorTop  + topOffset        (default 0)
```

The card inside is what actually changes width, as an ordinary WPF layout property, centred in a
frame that never moves. This is deliberate: a layered window being resized horizontally cannot be
composited in perfect step with its own content, so for a frame or two the content is drawn for
one width while the window is already at another, and everything centred - the header title above
all - visibly swings sideways. Measured on the header title during a hover-open, that swing was
29 px; with the window width held constant it is 1 px, which is sub-pixel text rounding.

The window is re-anchored on DPI changes, resolution changes and display reconfiguration. A saved
monitor that has been unplugged falls back to the primary display.

### One owner for state, one owner for geometry

Two classes divide the whole interaction layer between them, and nothing else is allowed in.

`OverlayStateMachine` decides **which panel is showing**. It owns the level, the overlay on top of
it, the pin, the popup count and hover intent. Every request goes through one idempotent
`RequestLevel(target, reason)`: asking for the level that is already current does nothing at all,
a newer request supersedes the one in flight, and each accepted transition carries an identifier
so a superseded one can no longer apply anything. It is pure - no WPF types, no timers, no
dispatcher - and hover intent is expressed as deadlines against an injected instant, which is why
the hysteresis can be tested exactly instead of by sleeping.

`NotchGeometryCoordinator` decides **how large the card is and where the window sits**. It is the
only code in the app that calls `SetWindowPos`. Card width, card height and the bottom corner
radius advance together on one monotonic `Stopwatch`, driven from `CompositionTarget.Rendering`;
each frame the card is given its new size first and only then is the window height applied, and
the window is moved at most once per rendered frame. An interrupted transition restarts from the
geometry actually on screen, so reversing a half-finished animation continues smoothly rather than
snapping back to where the previous one began.

Views and child controls may request a target state. They never write geometry, never start an
animation of their own, and no `SizeChanged`, `LocationChanged` or `LayoutUpdated` handler is
allowed to reposition anything - which is what removes the feedback loop that used to make the
panel oscillate.

### Hover hysteresis

- Open after about 220 ms, close after about 450 ms.
- Entering cancels a pending close; leaving cancels a pending open.
- Only the root window boundary reports hover. Moving between child controls, or between the notch
  and its expanded content, never reaches the state machine and so can never collapse anything.
- A click pins the panel; a pinned panel ignores pointer exit.
- An open overlay, editor, confirmation, tooltip or duration picker blocks auto-collapse, and an
  owned popup taking focus is not read as the user leaving.
- Closing a panel deliberately holds hover opening off until the pointer has genuinely left.
  Collapsing shrinks the window out from under the pointer, and without that hold the two effects
  chase each other: close, hover, open, close.
- Escape closes the overlay first, then the planner, then the quick view.

- `WS_EX_TOOLWINDOW` is applied and `WS_EX_APPWINDOW` removed, so it is absent from Alt+Tab and
  the taskbar.
- `WS_EX_TRANSPARENT` is deliberately **not** applied: the notch has to stay interactive.
- The root container has a null background, so the area around the card is not hit-testable:
  clicks pass through to whatever is underneath, and hover intent is raised by the card, not by
  the window rectangle. Both were verified by pointing at the gutter and watching focus and panel
  state stay where they were.
- `ShowActivated` is false and every reposition uses `SWP_NOACTIVATE`, so the notch never steals
  keyboard focus. It activates only when you click into a text field.
- No work-area space is reserved.

Animation is 200 ms opening with a cubic ease-out and 160 ms closing with a cubic ease-in-out: width, height,
corner radius, a content cross-fade and a small downward slide. When Windows animations are
turned off, every transition applies instantly and the app stays fully usable.

## Project layout

```
Counter.sln
build.ps1
run.ps1
README.md
THIRD_PARTY_NOTICES.md
Assets/
  Icons/Fluent/                    the 47 upstream SVGs, their licence and their checksums
tools/
  icons.psd1                       the icon list, the pinned release and the exact commit
  Sync-FluentIcons.ps1             downloads, verifies and regenerates the icon catalog
src/
  Counter.Core/                 net8.0, no Windows dependencies
    Abstractions/                  repository and settings interfaces
    Drafts/                        TaskDraft, DraftStore
    Focus/                         FocusEngine, FocusSessionService, TimeLedger, TimeFormat
    Journey/                       JourneyActivityService, ActivitySnapshot, DayActivity, IActivityReader
    Colour/                        Oklch conversion and gamut mapping, AccentEngine
    Models/                        TaskItem, FocusSession, FocusSegment, ManualTimeEntry,
                                   AccentPalette, GlassMaterial, enums, setting keys
    Statistics/                    StatisticsCalculator, StatisticsService, ITaskTimeReader
    Streaks/                       StreakCalculator, HeatmapCell
    Threading/                     IBackgroundScheduler
    Time/                          IClock, SystemClock
    Validation/                    TaskValidator
  Counter.App/                  net8.0-windows10.0.19041.0, WPF + WinForms tray
    Controls/                      AppIcon, IconCatalog (+ generated), IconButton, CircularBadge,
                                   CompletionCheck, LiquidGlassPanel, OklchStrip,
                                   JourneyHeatmapControl, ActivityChartControl
    Converters/                    value converters
    Data/                          FocusDatabase, migrations, SQLite repositories, activity reader, demo data
    Interop/                       Win32 declarations
    Theme/                         Colors, Brushes, Typography, Dimensions, Controls,
                                   ThemePalette, ThemeService, GlassNoise, ColourInput
    Services/                      paths, log, diagnostics, frame monitor, backups, export and
                                   restore, monitors, tray, hotkeys, startup, chime, dpi,
                                   BackdropService, AcrylicBackdrop, BackdropHost
    ViewModels/                    ShellViewModel (+ .Settings), OverlayStateMachine, StatisticsViewModel and friends
    Views/                         NotchWindow, NotchGeometryCoordinator
tests/
  Counter.Tests/                xUnit
```

## Tests

```powershell
dotnet test Counter.sln
```

711 tests, no sleeping anywhere: `TestClock` is an `IClock` the tests advance by hand, which is
what makes the timing assertions exact rather than flaky.

| Area | What is covered |
| --- | --- |
| Timer | countdown maths and formatting through 99:59:59, pause/resume drift, restart mid-session, expiry while closed, completion firing exactly once |
| Focus service | every row of the play contract, one session after every operation, transactional switching of session and run together, the double-click guard, a failed write leaving no false running state, startup repair of duplicate live sessions and of runs left open |
| Completion | completing the running task stops it, completing the paused task stops it, completing another task does not, elapsed time preserved, the reason stored, marking incomplete never restarting, the setting honoured, completion and a play press together leaving no inconsistent state |
| Time spent | runs opened and closed by every transition, paused time never counted, resume opening a second run, runs never overlapping, natural completion capped at the planned duration, manual stop and task completion recording what actually ran, an offline run capped at its target, manual time counted once, totals never double-counting, midnight and timezone splits, live time added from memory with no query |
| Duration | 1h, 2h, 24h and 99:59:59 round-tripping, formatting at every width, per-column clamping, steppers never carrying, presets, the minimum and maximum, the value preserved on reopen |
| Memory | a running timer surviving exit, an expired one reconciled once at its own target, a paused one restoring exactly, a stopped one never restarting, a crashed run closed rather than left growing, duplicate live sessions repaired, drafts written after a pause and restored, drafts cleared on save and on cancel, the day, filter, range and theme surviving a restart, backups at most daily and not disturbing the live file |
| Theme | one key set across both themes and all six accents, every value an eight-digit colour, every brush a view uses existing in the palette, no hardcoded colour outside the declaration, dark surfaces separated and not black, light text meeting 7:1 and 4.5:1, heatmap levels climbing in all twelve combinations, System resolving to Windows, the choice round-tripping |
| Accent | a palette being a name and one colour, the six base colours, every derived ramp complete and eight-digit, every ramp getting darker and staying inside one hue family, the hue surviving to two degrees at every stop, the ink being the more readable of the two, a pale accent never being handed white text, a colour outside the usable band being brought into it rather than refused, a custom colour going through the same engine, a stored identifier round-tripping, a bad one falling back rather than throwing, running wearing the accent, paused and warning and error and completed never following it, the glass staying neutral while the accent moves, every accent-driven key moving when the family changes, theme and accent moving independently, five stops at 0/18/48/76/100 on one light direction for every gradient in the app, the halo capped at twelve percent, the ambient reflection capped at a tenth, the structural contour reading in both themes, the grain monochrome and cached |
| Glass | the hairline being one physical pixel at 100, 125, 150 and 200 percent, a nonsense scale falling back rather than dividing by zero, a padded contour rasterising to exactly one device pixel on all four sides at all four scales, every light overlay white and thrown from the upper left, no view declaring an effect of its own, every shadow agreeing with the light direction, glass translucent but opaque enough to carry text, a glyph never below three to one on its own ramp, white preferred where white works, completion keeping its own family whatever the accent is, no gradient filling a surface, every glass surface a flat colour, the panel's ten layers in order, and every hairline resolving dynamically |
| Icons | the bundled SVGs matching their checksums, the licence and the attribution present, every asset being official single-colour artwork, every kind resolving through the variant fallback, every geometry frozen and inside its own viewBox and on its own centre, the required mapping pointing at the named upstream files, corrections centralised and capped, no Path or symbol font or text glyph or emoji in any view, every icon-only button carrying a tooltip and a name, no icon padded by hand, no retired icon key surviving |
| Rendering | every icon centred in its host at 96, 120, 144 and 192 DPI, none spilling out of it, the play triangle centred on its mass, badges the same size and place for 1, 8, 11, 20, 28 and 31 at every scale, and centred in their cell |
| Settings | the settings command opening Settings only, the statistics command opening Statistics only, each closing the other, each button being its own way back, returning to the panel it was opened from, Escape leaving without collapsing, the accent read at start-up and reported back, an unreadable accent falling back, the default duration written only once it is valid, behaviour toggles asking rather than assuming |
| Controls | a badge staying square whatever it holds and whatever room it is offered and never growing to fill a larger cell, its content centred and rounded to whole pixels, its baseline correction capped, completion not changing any dimension, an icon measuring to its own square for every kind, an optical correction over a pixel refused |
| Tray | a populated display submenu refilling without throwing, entries never accumulating across repeated refills, the replaced entries actually disposed, and an empty submenu filling on the first pass |
| Glass | the three materials and their order, each letting strictly more through than the one before in both density tables, a blur behind buying a much thinner sheet, the tint packed the way the accent policy reads it, whether Windows will blur at all being asked rather than assumed and never offered when transparency is off, a stored value round-tripping, an unreadable one falling back rather than throwing, each material letting strictly more through than the one before it, none transparent enough to read a sentence through, a translucent material given stronger ink, both themes describing every material identically, a material only ever restating a key the theme already declares, the material never touching a single accent colour, both reflections being white light and nothing else, the far edge running from the corner opposite the source, neither reflection strong enough to read as a surface, the ripple cached and monochrome and seamless across its own wrap, and slower and stronger than the grain |
| Custom accent | a colour read the way a person would write it - a name, three digits, six, eight, hash or none - anything else refused rather than thrown, a transparency dropped rather than carried, a hand-mixed colour going through the identical derivation, its identifier round-tripping through storage, the brightness strip offering exactly the engine's band and no wider, hue joining at both ends while the other two clamp, a strip reachable by assistive technology and reporting its own range, the seventh swatch being a door rather than a family, a drag previewing without storing and committing once, the strips and the text never disagreeing, typing a colour moving the thumbs to it, typing a non-colour restoring what is selected, a stored custom accent coming back selected and loaded, choosing a named family leaving the mixed colour where it was, and reporting a colour back never asking for it again |
| Heatmap | exactly eighty-four dates, Monday through Sunday alignment, today in the last column, future days inactive, levels mapping, yesterday updating immediately, uncompletion removing it, manual time activating a date, tooltips, and a real render at 96, 120, 144 and 192 DPI with hard edges and equal square widths |
| Statistics | all four ranges, hourly and daily and weekly bucketing, task and session counts, focus aggregation, average session, completion rate with and without scheduled tasks, both streaks, top-task ordering and capping, a deleted task leaving the list while its hours stay in the total, a deleted task counting in neither the per-task average nor the busiest day, a task whose row is gone entirely never being resurrected from a session title, the per-task average dividing by tasks worked on rather than tasks that exist, the daily average dividing by days worked rather than days in the range, the best day, the busiest weekday summing every week in the range, the busiest day counting completions rather than time, the longest run being one session rather than a day's total, an empty range reporting nothing rather than zero beside a meaningless date, midnight splits, a range clipping a run that started before it, live time included without being written, an empty database producing zeros |
| Overlay machine | five hundred randomised requests landing exactly where last asked, duplicate requests as no-ops, newer requests superseding older ones, stale identifiers never becoming current, a hundred open-close cycles leaving no residue, hover hysteresis in both directions, child-to-child movement never collapsing, popups blocking collapse, a press on the collapsed notch not pinning it, Escape releasing a stuck pin, Escape ordering across four levels |
| Geometry | the window width and position never moving with the card, five hundred randomised sizes at 100, 125, 150 and 200 percent, the horizontal centre and the top edge holding, whole-pixel rounding, a monitor left of the origin, a mixed-DPI secondary monitor, the card clamped to small displays |
| Journey | completing a task scheduled yesterday, backfilling reconnecting a streak, removing a connecting day breaking it, unfinished tasks counting for nothing, uncompleting and deleting removing contributions, manual entries contributing, moving a completed task moving its contribution, a task and a session together, future dates never extending a streak, timezone changes not moving stored dates, one snapshot behind both panels, a timer tick never recomputing |
| Storage | schema creation, migration from hand-built schema 1 and schema 2 files preserving every row, historical runs reconstructed, long durations round-tripping, migration idempotency, a failed migration leaving the file untouched, refusal of a newer schema, contribution queries, atomic batch writes rolling back whole, foreign keys, soft deletion keeping sessions attached |
| Validation | titles, notes and durations |

## Accessibility

Every action is reachable from the keyboard, focus indicators are visible but restrained, icon-only
buttons carry tooltips and accessible names, interactive targets are at least 28 x 28, and the
window is per-monitor-v2 DPI aware. There is no continuous animation and no polling; the display
refreshes twice a second while a session runs and the app idles at negligible CPU. Tray icons,
timers, event handlers, database connections and registered hotkeys are all disposed on shutdown.
UI-thread exceptions are logged and shown as a non-destructive banner rather than taking the app
down.

## Known deviations from the brief

- **Panel heights are measured rather than the sketched 148-158 px.** A 42 px notch header plus a
  section header, three two-line task rows at 34 px and an "Add a task" footer comes to roughly
  210 px however tightly it is packed; 158 only works with two rows. Rather than clip a control or
  pad the panel out, each state measures its own content. Say the word and I will drop the quick
  view to two rows to hit 158 exactly.
- **Display scaling was verified by rendering, not by re-scaling the desktop.** Both displays on
  this machine run at 100 percent, and changing system scaling relayouts every open window the
  user has. So the crispness claim is tested where it can be tested exactly: the heatmap, every
  icon and the circular badge are each rendered to a bitmap at 96, 120, 144 and 192 DPI and their
  pixels are inspected, and `GeometryTests` asserts at the same four factors that the window's
  width and position never move, that the centre and the top edge hold across five hundred
  randomised sizes, and that every rectangle lands on whole device pixels. What has *not* been
  looked at with human eyes is how the hairlines and the 1 px edges of the rest of the interface
  resolve on a fractional grid.
- **The composed control templates are verified on screen at 100 percent, not rendered at four
  DPIs.** A template lives in a compiled resource dictionary, and loading one in a test needs an
  `Application` and a pack URI that a test process does not have. So the render tests exercise
  `AppIcon` and `CircularBadge` directly - which is where the centring rules actually live - and
  the assembled checkbox, calendar cell and header were checked by screenshot and pixel
  measurement on the running application instead.
- **The 95th-percentile frame interval is at the target, not comfortably inside it.** Across 182
  ordinary transitions the median frame interval was 9.84 ms and the median p95 was 19.70 ms,
  just under the 20 ms target, with 14 frames out of 3250 over 33 ms. Under a deliberately hostile
  burst - a new transition every 70 ms, so 151 of 162 were superseded mid-flight - the median p95
  rose to 20.92 ms and the worst single frame reached 55.80 ms. Nothing was left in the wrong
  state by any of it: every transition either settled or was cleanly superseded, and the final
  geometry was always the one last asked for.
- **Notifications use `NotifyIcon.ShowBalloonTip`**, which Windows 10 and 11 surface as a normal
  notification. A WinRT toast would need a packaged identity (MSIX), which the no-installer,
  no-admin constraint rules out.
- **The tray menu was not click-verified.** The icon is created and visible in the notification
  overflow, but Windows 11's hidden-icon flyout ignores synthetic mouse and keyboard input, so the
  menu items, the theme and accent submenus and Quit could not be exercised end to end from here.
  The commands behind them are the same ones the notch, Statistics and Settings use and are
  covered by the test suite; what is unverified is the flyout itself.
- **A restore takes effect at the next start rather than immediately.** Swapping a database file
  underneath a live connection is the one operation in this application that could genuinely lose
  history. The chosen backup is checked and staged instead, and the swap happens before anything
  opens the file on the next launch. It is deliberate, and the panel says so.
- **The two translucent glasses keep their contour, and the reference designs have none.** Both
  reference cards do their edge with an inset glow and `border: 0`, which is right for a card
  inside a document that already frames it. This panel sits on top of whatever the desktop happens
  to be, and the brief that preceded these two is explicit that the whole tool must always have a
  visible outline. So the structural ring stays and the rim is added inside it.
- **The frosted shadow is mirrored.** Its reference offsets it `-4px 12px`, which is a light
  source at the upper right; everything in this application is lit from the upper left, and one
  light source is worth more than one borrowed number. The blur, the distance and the weight are
  the reference's; only the sign of the horizontal offset changed. The far-edge reflection was
  *not* mirrored, because "brightest at the corner opposite the light" happens to be the same
  corner either way.
- **`saturate(60%)` and the turbulence displacement are not reproduced.** Both read the backdrop
  and then transform it, which the companion window can only do by handing DWM a filter it does
  not accept: acrylic is a blur and a tint, and that is the whole vocabulary. The blur itself is
  real; the saturation shift is not, and the displacement is reinterpreted as a texture over the
  glass rather than a distortion of what is under it.
- **The blur is a second window rather than the panel's own backdrop.** The consequence is that
  it tracks the panel rather than being part of it. In practice that is invisible - it is placed
  in the same layout pass and only told to move when it has actually moved - but it is one more
  thing that has to stay in step, and it is the reason Solid glass creates no such window at all.
- **Solid glass is denser than any reference, on purpose.** It is the default because it is the
  only one of the three that does not depend on the compositor being willing to blur.
- **The concept image shows a white glyph on the orange play button; this shows a dark one.**
  White on that orange measures 2.2:1, which is under the 3:1 WCAG asks of a graphical object and
  well under the 4.5:1 it asks of text. The brief's own correction is explicit that the foreground
  must be measured and that white must never be forced onto a gradient that cannot carry it, and
  it names pale colours as the case to watch. So the ink is measured: white on blue, purple and
  pink, dark on cyan, green and orange. It is the one place where following the written rule and
  matching the reference render disagree, and the rule wins.
- **Display scaling is verified by rendering rather than by re-scaling the desktop.** Both
  displays on this machine run at 100 percent, and changing system scaling relayouts every window
  the user has open. The hairline is therefore asserted arithmetically and then rasterised and
  counted at 96, 120, 144 and 192 DPI - and the render test deliberately does not force a DPI
  context onto the visual, because WPF's `SetRootDpi` leaks process-wide state into every other
  render test running beside it. What that leaves untested is the running window's own layout
  rounding at a fractional scale, which is WPF's behaviour rather than this code's.
- **Statistics for "all time" bound the query at twenty years.** An unbounded range needs no
  bound in principle, but the date arithmetic has to be finite somewhere. The chart still starts
  at the first day that actually holds something, so the bound is invisible in practice.

## Diagnostics

`Diag` writes a channel-tagged trace of the interaction layer to
`%LocalAppData%\Counter\logs\diag.log`: panel transitions with their reason and identifier,
hover enter and leave with the pin and blocking state, window activation, geometry starts and
settles with their from and to sizes, play requests with the resulting outcome and session state,
theme applications, journey and statistics refreshes, draft saves, and manual time entries.

`FrameMonitor` adds the rendering side. While a transition is in flight it records the interval
between composed frames and reports, on settle, the frame count, the median, the 95th percentile,
the worst single frame and how many ran over 33 ms - together with whether the transition finished
or was superseded. Superseded transitions and stale callbacks are counted separately. That is the
only honest way to claim the interface is smooth: sixty frames a second is a statement about
intervals, not about code.

WPF's own data-binding failures are routed into the same trace. A broken binding is silent at
runtime - the control simply shows nothing and carries on - so sending them here makes "there are
no binding errors" something that can be checked rather than assumed.

All of it is compiled out of Release builds unless `COUNTER_DIAG` is set in the environment,
so a normal build writes nothing and measures nothing. It is what a twitch, a dropped click or a
stale streak should be diagnosed from: the trace shows the exact event sequence rather than the
end state.

`Log` is separate and stays on in Release; it records only things a user might have to act on.

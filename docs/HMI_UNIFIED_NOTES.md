# WinCC Unified through Openness: field notes

Things learned the hard way while building real WinCC Unified screens with this server (TIA Portal V20, Unified
Comfort panel). Most of it is about Openness and the Unified runtime, not about this server, and none of it is
obvious from the documentation.

## The object model

- A Unified panel's software is `Siemens.Engineering.HmiUnified.HmiSoftware`. It is a different type, in a
  different namespace, from the classic `Siemens.Engineering.Hmi.HmiTarget` (Comfort/Basic panels). Code written
  for one finds nothing on the other. `tia_list_devices` tells you which you have (`hmiUnified` / `hmiClassic`).
- Unified screens have a real object API: `Screens.Create(name)`, `ScreenItems.Create<T>(name)`. Classic screens
  have none - they can only be exported and imported as XML.
- Screens can live in screen groups. This server finds screens by name wherever they are.

## Creating items

- `tia_debug_screen_item_creation_infos` lists the item types Openness declares as creatable. In V20 that covers
  buttons, IO fields, symbolic IO fields, text boxes, text and the basic shapes (rectangle, circle, ellipse, line,
  polygon...), bars, gauges, sliders, list boxes, check box / radio groups, toggle switches, clocks, touch areas,
  graphic views, screen windows, faceplate containers and the alarm, trend and parameter controls. There is **no
  table/grid** among them: if you need one, add it by hand in TIA Portal and bind it through the server.
- **Being listed is not a guarantee.** `HmiLabel` is listed, yet `Create<HmiLabel>()` always fails with "Not
  supported". For static text use `HmiText` (a shape, not a widget); `tia_hmi_create_label` does that. Only
  buttons, IO fields, text and rectangles are exercised by this project's tests - try other types on a scratch
  screen first.
- **Z-order is creation order.** An item created later is drawn on top. To put a label over a rectangle, create
  the rectangle first - or delete and recreate the label.
- Set the screen size when you create the screen. A screen can be made larger than the panel's resolution;
  check on the device how your panel presents that before designing around it.
- Images cannot be uploaded through Openness. Import them into the project graphics in TIA Portal first, then
  refer to them by name (`tia_hmi_set_button_graphic`).

## Text

- Display texts are stored as **HTML**: `<body><p>Start</p></body>`. Assigning plain text fails with
  "The argument 'text' has an invalid format". The server wraps and escapes plain text for you and strips the
  markup again when it reads a text back.

## Numbers on IO fields

- `OutputFormat` uses Unified's own syntax, **not** a .NET format string: `{F0}` for no decimals, `{F1}` for one,
  `{F2}` for two. TIA Portal accepts `"0.0"` without complaint, and the panel then shows a default format with
  many decimals.
- `IOFieldType` must be `InputOutput` for the operator to be able to enter a value; `Output` is display only.

## Tags

- An HMI tag is linked to the PLC by setting `Connection` **first** and `PlcTag` second. The other way round TIA
  reports that the controller tag was not found.
- Once linked, the tag's data type follows the PLC tag and becomes read-only ("Set is not allowed for disabled
  fields").
- PLC address syntax: `"DB_Name".member` (TIA shows it back as `DB_Name.member`); array elements work too:
  `"DB_Name".values[3]`.
- The PLC block behind the tag must be **compiled and consistent**, otherwise linking fails with "controller tag
  not found" even though the name is right.
- Acquisition cycles are named with a `T` prefix: `T100ms`, `T500ms`, `T1s`. `100ms` is rejected.
  The default cycle is slow; lower it for anything the operator expects to react immediately.
- Internal tags (no connection) have no acquisition cycle.

## Making things change with a tag

- **Colour by a Bool tag**: use a native tag dynamization with a two-entry mapping table
  (`tia_hmi_set_property_tag_color`). It is more reliable than a script.
- The mapping table has one entry per condition (`Condition` 0 = false, 1 = true). **Find the entries by their
  condition, never by position**: on a dynamization that already existed the order can be reversed.
- One dynamization gives you two colours. For three states, stack items and switch their `Visible` property with
  separate Bool tags.
- A tag binding without a mapping table passes the value straight through (`tia_hmi_bind_property_to_tag`): use
  it for `ProcessValue`, `Visible`, or a `Text` bound to a String tag. It cannot invert a Bool - provide the
  inverted signal from the PLC.
- **Copying an item in TIA Portal copies its dynamizations**, still pointing at the original's tag. After
  copy-paste, check the bindings (`tia_hmi_get_screen`) and re-point them.

## Scripts

- **Writing a tag from a script: use the system functions.**

  ```javascript
  HMIRuntime.Tags.SysFct.SetBitInTag("MyHmiTag", 0);     // set bit 0
  HMIRuntime.Tags.SysFct.ResetBitInTag("MyHmiTag", 0);   // reset bit 0
  HMIRuntime.Tags.SysFct.SetTagValue("MyHmiTag", 42);    // numeric value
  ```

  `Tags("MyHmiTag").Value = true` passes the syntax check and **never reaches the PLC**.
- A momentary push button is two handlers: `Down` sets the bit, `Up` resets it.
- Changing screen: `HMIRuntime.UI.SysFct.ChangeScreen("Screen name", ".");`
- The syntax check (`SyntaxCheck()`, run by the server whenever it sets a script) validates JavaScript syntax
  only. It does not know whether the tag exists or whether the function does what you hope. Test on the panel.

## Symbolic IO field (drop-down of named values)

1. Create the text list in TIA Portal (*Text and graphic lists*). Text lists cannot be edited entry by entry
   through Openness.
2. Create an `HmiSymbolicIOField` (`tia_hmi_create_widget`).
3. Set `IOFieldType = InputOutput` and `ResourceList = <text list name>` (`tia_hmi_set_widget_property`).
4. Bind `ProcessValue` to the HMI tag (`tia_hmi_bind_property_to_tag`).

## When the panel does not talk to the PLC

In the order worth checking:

1. **Which Ethernet port is the cable in?** Panels with two PROFINET interfaces have two IP addresses, usually in
   different subnets. *Accessible devices* in TIA Portal shows which one is actually connected.
2. **Subnet masks.** Two interfaces whose subnets overlap give confusing results.
3. **PLC protection settings** (*Protection & Security > Connection mechanisms*): the CPU's secure-communication
   setting has to match what the panel can do, and that is a separate switch from *Permit access with PUT/GET
   communication*. Siemens' FAQ on connecting WinCC Unified to S7-1200/1500 covers the combinations.
   `tia_dump_plc_protection` shows the current values.
4. A firmware version in the project that is slightly older than the panel's is usually *not* the problem.
5. `DisabledAtStartup` on the HMI connection is not the problem either unless you set it.

And remember: a change to a screen is on the panel only after *Download to device*. Saving the project is not
enough.

## Working with the server

- Read before you write: `tia_hmi_get_screen` shows every item with its position, text, bindings and scripts.
  The layout may have changed in TIA Portal since the last time you looked.
- Build a screen in one go with `tia_hmi_create_items`. It is one undo step, and a mistake in one item rolls the
  whole call back instead of leaving half a screen.
- Do not know which property to set? `tia_hmi_get_item` lists them with current values;
  `tia_debug_reflect_type` shows the values of an enum.
- After rebuilding the server exe, the MCP client must be restarted to see new tools, and TIA Portal asks for
  Openness access again.

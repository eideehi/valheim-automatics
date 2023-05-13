# Automatics - Valheim Mod
Automatics is a mod that automates the tedious tasks of life in Valheim.
Most of its features exist in existing mods, but it has been re-designed
to make it easier for me to use.

# Features

<div class="tip">

Each of the features described below can be completely disabled using
the "Disable Module" option in the configuration.

</div>

## Automatic door

Automatically opens and closes the door near the player. The interval
and distance to detect the player can be changed from the configuration.

## Automatic mapping

Automatic pinning animals, monsters, floras, veins, dungeons, etc. that
exist around the player to the map. The pinning allows for each object
and the detection range of the object can be changed from the config.

<div class="informalexample">

**Custom icon pack:**
You can also define your own icons in png and json files. See
[docs/custom-icon-pack.adoc](https://github.com/eideehi/valheim-automatics/blob/1.4.7/docs/custom-icon-pack.adoc)
for custom icon pack specifications.

[![Document - Custom Icon Pack](https://app.box.com/shared/static/ggj61oyrdik1jk08lohdqr91e1q5isqv.png)](https://github.com/eideehi/valheim-automatics/blob/1.4.7/docs/custom-icon-pack.adoc)

</div>

## Automatic processing

Refueling pieces that need fuel. Deliver materials to pieces that
process materials, and store items produced by pieces. These tasks can
be automated via containers around the piece.

## Automatic feeding

Modifies tamable creatures so that they can eat food from containers and
the player’s inventory, not just items on the ground.

## Automatic repair

It can automatically repair items when the player is near a crafting
station or when the crafting station GUI is opened or automatically
repair nearby pieces when the player has a hammer equipped.

## Automatic mining

Automatically mines minerals in the player’s surroundings. You can also
add a shortcut key that allows you to mine at any time.

## Automatic pickup

Automatically pick up items around the player. You can also add a
shortcut key that searches the surroundings for the same items as the
interacted item and pick up them all.

# Console commands

Automatics add a few commands to help the user.

## automatics

*Usage: automatics (OPTIONS…​)*

Displays the usage of commands added by Automatics.

### OPTIONS

<table>
<tbody>
<tr class="odd">
<td style="text-align: left;"><p><strong>-i,
--include=VALUE</strong></p></td>
<td style="text-align: left;"><p>Show only commands whose names include
the word specified in this option.</p></td>
</tr>
<tr class="even">
<td style="text-align: left;"><p><strong>-e,
--exclude=VALUE</strong></p></td>
<td style="text-align: left;"><p>Exclude commands whose names contain
the word specified with this option.</p></td>
</tr>
<tr class="odd">
<td style="text-align: left;"><p><strong>-v, --verbose</strong></p></td>
<td style="text-align: left;"><p>Displays detailed usage of the
command.</p></td>
</tr>
<tr class="even">
<td style="text-align: left;"><p><strong>-h, --help</strong></p></td>
<td style="text-align: left;"><p>Displays a help message and exits the
command.</p></td>
</tr>
</tbody>
</table>

## printnames

*Usage: printnames (OPTIONS…​) (WORD|REGEXP)…​*

Outputs internal or display names that contain the specified string or
match the specified regular expression. If multiple arguments are
specified, only those matching all of them will be output.

### WORD

A text contained in the internal or display name. (e.g. `$enemy_`,
`$item_`, `$piece_`, `Boar`, `Mushroom`, `Wood door`); All partially
matching internal and display names are output.

### REGEXP

A regular expression of the internal or display name to be output. Must
be prefixed with r/ (e.g. `r/^[$]item_`, `r/^boar$`)

### OPTIONS

<table>
<tbody>
<tr class="odd">
<td style="text-align: left;"><p><strong>-h, --help</strong></p></td>
<td style="text-align: left;"><p>Displays a help message and exits the
command.</p></td>
</tr>
</tbody>
</table>

### Examples

-   `printnames ling r/^[$]enemy_`

-   `printnames r/^[$@]location_.+(?<!_(enter|exit))$`

-   `printnames mushroom r/^[$]item_.+(?<!_description)$`

## printobjects

*Usage: printobjects \[TYPE\] (OPTIONS…​)*

Display objects that can be handled by Automatics in the nearby.

**NOTE: The result of this command may show objects that you feel are not of the
target type. For example, a Flint appear in the Flora search. This is
not a bug but means that Automatics can treat Flint as Flora.**

### TYPE

Type of object to be displayed. Specify one of the following: animal,
container, door, dungeon, flora, mineral, monster, spawner, spot,
vehicle, other.

### OPTIONS

<table>
<tbody>
<tr class="odd">
<td style="text-align: left;"><p><strong>-r,
--radius=VALUE</strong></p></td>
<td style="text-align: left;"><p>Specify the range within which the
object is to be searched. [Default: 32] (Unit: Meters)</p></td>
</tr>
<tr class="even">
<td style="text-align: left;"><p><strong>-n,
--number=VALUE</strong></p></td>
<td style="text-align: left;"><p>Specify how many objects matching the
condition are to be displayed. [Default: 4]</p></td>
</tr>
<tr class="odd">
<td style="text-align: left;"><p><strong>-i,
--include=VALUE</strong></p></td>
<td style="text-align: left;"><p>Show only objects whose internal or
display names match the word specified in this option. It works as a
regular expression by concatenating r/ at the beginning of the
string.</p></td>
</tr>
<tr class="even">
<td style="text-align: left;"><p><strong>-e,
--exclude=VALUE</strong></p></td>
<td style="text-align: left;"><p>Exclude objects whose internal names or
display names match the word specified with this option. It works as a
regular expression by concatenating r/ at the beginning of the
string.</p></td>
</tr>
<tr class="odd">
<td style="text-align: left;"><p><strong>-h, --help</strong></p></td>
<td style="text-align: left;"><p>Displays a help message and exits the
command.</p></td>
</tr>
</tbody>
</table>

## removemappins

*Usage: removemappins (OPTIONS…​)*

Remove map pins that match the specified conditions. If no options are
specified, all duplicate pins will be deleted.

**NOTE: Please disable the "Automatic Mapping" feature before using this
command. It may cause malfunctions.**

### OPTIONS

<table>
<tbody>
<tr class="odd">
<td style="text-align: left;"><p><strong>-r,
--radius=VALUE</strong></p></td>
<td style="text-align: left;"><p>Specify the maximum distance from the
player’s position to the pin to be removed. If set to 0, all pins will
be targeted. [Default: 0] (Unit: meters)</p></td>
</tr>
<tr class="even">
<td style="text-align: left;"><p><strong>-i,
--include=VALUE</strong></p></td>
<td style="text-align: left;"><p>Pins that contain the specified string
in their name will be targeted for deletion.</p></td>
</tr>
<tr class="odd">
<td style="text-align: left;"><p><strong>-e,
--exclude=VALUE</strong></p></td>
<td style="text-align: left;"><p>Pins that contain the specified string
in their name will be excluded from the deletion target.</p></td>
</tr>
<tr class="even">
<td style="text-align: left;"><p><strong>-a,
--allow_non_duplicate_pins</strong></p></td>
<td style="text-align: left;"><p>non-duplicate pins in the deletion
targets. <strong>[Note] Improper use of this option may result in the
deletion of all pins, including those added by the
player.</strong></p></td>
</tr>
<tr class="odd">
<td style="text-align: left;"><p><strong>-h, --help</strong></p></td>
<td style="text-align: left;"><p>Displays a help message and exits the
command.</p></td>
</tr>
</tbody>
</table>

# Configurations

I recommend using [Configuration
Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager).

[![Configuration Menu (Click to view full size)](https://app.box.com/shared/static/3v57rjpauzzyv0xeugohnw8bn2ye3q2h.png)](https://app.box.com/shared/static/vfzsn69i950l48er2u69tssod6xxsh8u.jpg)

**The README would be too large if we described all the details of the
configuration, so we split it into separate file.**

Open
[CONFIG.adoc](https://github.com/eideehi/valheim-automatics/blob/1.4.7/CONFIG.adoc)
to see the configuration details.

## Adding object definitions to Automatics

You can use the [Configuration
Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager) to
define objects that you want Automatics to work with.

![User-defined objects in GUI](https://app.box.com/shared/static/5f6dvpg1elczu9froqkepxamv03ci9cd.png)

Open
[docs/add-user-defined-object.adoc](https://github.com/eideehi/valheim-automatics/blob/1.4.7/docs/add-user-defined-object.adoc)
to learn more about adding user-defined objects.

## About deprecated options

### Resources Directory / \[resources_directory\]
Provided to load custom icon packs, this option will be discontinued in
the near future as Automatics can now load custom icon packs from the
BepInEx plugin folder.

# Supplementary explanation

## Matching by "Display name" and "Internal name"

In some features of Automatics, there is an option that allows the user
to add targets as needed. The "Display name" and "Internal name" are
used to identify these targets. The display name and internal name are
matched according to different rules.

### Display name
Display names are the names that appear in the game, such as Boar, Deer,
Dandelion, etc. The matching rule for "Display name" is a partial match,
meaning that if the target display name contains the specified string,
it matches. It is case-insensitive.

### Internal name
Internal names are the names used inside the game program, such as
`$enemy_boar`, `$enemy_deer`, `$item_dandelion`, etc. The matching rule
for "Internal name" is an exact match, meaning that if the target
internal name is identical to the specified string, it matches. It is
case-insensitive. Note that internal names for translations added by
Automatics are prefixed with `@`, not `$`, as in `@internal_name`

### Matching Samples

| Display name | Internal name    |
|--------------|------------------|
| Greyling     | $enemy_greyling  |
| Greydwarf    | $enemy_greydwarf |
| Surtling     | $enemy_surtling  |

Target data

|           | Grey     | ling     | $enemy_greyling | $enemy_greydwarf | $enemy\_ |
|-----------|----------|----------|-----------------|------------------|----------|
| Greyling  | Match    | Match    | Match           | No match         | No match |
| Greydwarf | Match    | No match | No match        | Match            | No match |
| Surtling  | No match | Match    | No match        | No match         | No match |

Matching result

# Languages

| Language | Translators       | Status |
|----------|-------------------|--------|
| English  | Translation Tools | 100%   |
| Japanese | EideeHi           | 100%   |

# Contacts

[![Bug report on Issues](https://app.box.com/shared/static/g2v3vbju4jazq7kycoigp60ltki2kw8i.png)](https://github.com/eideehi/valheim-better-portal/issues)

**Only bug reports are accepted under Issues.**

[![eidee.net - Discord Server](https://app.box.com/shared/static/0s09ti60hvyyp5k98xyrnkfp683mrt9r.png)](https://discord.gg/DDQqxkK7s6)

**Questions, suggestions and comments are welcome on the Discord Server.**

# Credits

-   Dependencies:

    -   [Configuration
        Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager)

    -   [LitJSON](https://litjson.net)

    -   [NDesk.Options](http://ndesk.org/Options)

# License

Automatics is developed and released under the MIT license. For the full
text of the license, please see the
[LICENSE](https://github.com/eideehi/valheim-automatics/blob/1.4.7/LICENSE)
file.

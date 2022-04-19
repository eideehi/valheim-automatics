# Automatics - Valheim Mod
Automatics is a mod that automates the tedious tasks of life in Valheim.
Most of its features exist in existing mods, but it has been re-designed
to make it easier for me to use.

## Features

### Automatic door

Automatically opens and closes the door near the player. The interval
and distance to detect the player can be changed from the configuration.

### Automatic map pinning

Automatic pinning animals, monsters, floras, veins, dungeons, etc. that
exist around the player to the map. The pinning allows for each object
and the detection range of the object can be changed from the config.

**Custom map icons**

Using png files and json, you can customize icons for pins added by
Automatic map pinning.

![Custom Map Icons](https://app.box.com/shared/static/ggj61oyrdik1jk08lohdqr91e1q5isqv.png)

**Directory structure**

Place the necessary files in
"/Valheim/BepInEx/plugins/Automatics/Textures" to make the custom icons
work.

-   **/Valheim/BepInEx/plugins/Automatics/Textures**

    -   **custom-map-icon.json**

        -   Describe information such as which icons to customize and
            which images to use. Details are described in the
            `Format of custom-map-icon.json` section.

    -   **ICON.png**

        -   Icon image file. PNG files with transparency information are
            preferred. Prepare the number of icons required.

**Format of custom-map-icon.json**

Root &lt;Array&gt;
-   target &lt;Object&gt;

    -   name &lt;String&gt;

        -   Specify a display name such as "Boar" or an internal name
            such as "$enemy\_boar"; See the `Matching by "Display name" and "Internal name"` section
            for the difference between matching by display name and
            internal name.

    -   metadata &lt;Object&gt; (Optional)

        -   level &lt;Number&gt;

            -   By setting up this field, you can set up icons for
                different levels of animals and monsters. Note that the
                unstarred state is level 1. One star is level 2 and two
                stars are level 3. Also, do not set the metada field if
                matching by level is not required.

-   sprite &lt;Object&gt;

    -   file &lt;String&gt;

        -   Specify the name of the image file to be used for the icon.
            It is best to keep the image size between 16x16 and 32x32,
            as too large an icon will be obtrusive.

    -   width &lt;Number&gt;

        -   Specify the width of the icon.

    -   height &lt;Number&gt;

        -   Specify the height of the icon.

**Example files**

I don’t feel I have explained it very well, so I have prepared a sample
file. Please click
[here](https://github.com/eideehi/valheim-automatics/tree/main/package/extra/custom-icon-example/Automatics/Textures) to check
the structure of the file that actually works. You can also download the
[zip
file](https://app.box.com/shared/static/dv8vd8ls83rzxcsl75zrh9rawlu5o2w7.zip)
and check the operation on your PC.

-   [Example files
    (Github)](https://github.com/eideehi/valheim-automatics/tree/main/package/extra/custom-icon-example/Automatics/Textures)

-   [Example files
    (Zip)](https://app.box.com/shared/static/dv8vd8ls83rzxcsl75zrh9rawlu5o2w7.zip)

NOTE: Zip is not guaranteed to work with mod loaders other than Vortex; if you are using a mod loader other than Vortex, please manually place the files in the Zip into the appropriate directory.

### Automatic processing

Refueling pieces that need fuel. Deliver materials to pieces that
process materials, and store items produced by pieces. These tasks can
be automated via containers around the piece.

### Automatic feeding

Animals and other creatures that consume food will be able to consume
food from containers and the player’s inventory, not just from food on
the ground.

### Automatic repair

It can automatically repair items when the player is near a crafting
station or when the crafting station GUI is opened or automatically
repair nearby pieces when the player has a hammer equipped.

## Console commands

Automatics add a few commands to help the user.

### automatics \[COMMAND\]

Displays the usage of commands added by Automatics.

-   COMMAND: Command name to display usage. If there is an exact match,
    it will be displayed. Otherwise, it will display the first partially
    matched command found. If omitted, the usage of the automatics
    command itself is displayed.

### printnames (WORD|REGEXP)...

Outputs internal or display names that contain the specified string or match the specified regular expression. If multiple arguments are specified, only those matching all of them will be output.

-   WORD: A text contained in the internal or display name. (e.g.
    $enemy\_, $item\_, $piece\_, Boar, Mushroom, Wood door); All
    partially matching internal and display names are output.

-   REGEXP: A regular expression of the internal or display name to be
    output. Must be prefixed with "r/". (e.g. r/^\[$\]item\_, r/^boar$)

Examples:

- `printnames ling r/^[$]enemy_`
- `printnames r/^[$@]location_.+(?<!_(enter|exit))$`
- `printnames mushroom r/^[$]item_.+(?<!_description)$`

## Configurations

I recommend using [Configuration
Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager).

![Configuration Menu](https://app.box.com/shared/static/3v57rjpauzzyv0xeugohnw8bn2ye3q2h.png)

Please check the [details from GitHub](https://github.com/eideehi/valheim-automatics/blob/main/CONFIG.adoc) as the README will be too large to upload to Thunderstore if all the optional details are described.

## Supplementary explanation

### Matching by "Display name" and "Internal name"

In some features of Automatics, there is an option that allows the user
to add targets as needed. The "Display name" and "Internal name" are
used to identify these targets. The display name and internal name are
matched according to different rules.

#### Display name

Display names are the names that appear in the game, such as Boar, Deer,
Dandelion, etc. The matching rule for "Display name" is a partial match,
meaning that if the target display name contains the specified string,
it matches. It is case-insensitive.

#### Internal name

Internal names are the names used inside the game program, such as
$enemy\_boar, $enemy\_deer, $item\_dandelion, etc. The matching rule for
"Internal name" is an exact match, meaning that if the target internal
name is identical to the specified string, it matches. It is
case-insensitive. Note that internal names for translations added by Automatics are prefixed with @, not $, as in `@internal_name`

#### Matching Samples

Target data
<table>
<thead>
<tr class="header">
<th style="text-align: left;">Display name</th>
<th style="text-align: left;">Internal name</th>
</tr>
</thead>
<tbody>
<tr class="odd">
<td style="text-align: left;"><p>Greyling</p></td>
<td style="text-align: left;"><p>$enemy_greyling</p></td>
</tr>
<tr class="even">
<td style="text-align: left;"><p>Greydwarf</p></td>
<td style="text-align: left;"><p>$enemy_greydwarf</p></td>
</tr>
<tr class="odd">
<td style="text-align: left;"><p>Surtling</p></td>
<td style="text-align: left;"><p>$enemy_surtling</p></td>
</tr>
</tbody>
</table>

Matching result
<table style="width:100%;">
<thead>
<tr class="header">
<th style="text-align: left;"></th>
<th style="text-align: left;">Grey</th>
<th style="text-align: left;">ling</th>
<th style="text-align: left;">$enemy_greyling</th>
<th style="text-align: left;">$enemy_greydwarf</th>
<th style="text-align: left;">$enemy_</th>
</tr>
</thead>
<tbody>
<tr class="odd">
<td style="text-align: left;"><p>Greyling</p></td>
<td style="text-align: left;"><p>Match</p></td>
<td style="text-align: left;"><p>Match</p></td>
<td style="text-align: left;"><p>Match</p></td>
<td style="text-align: left;"><p>No match</p></td>
<td style="text-align: left;"><p>No match</p></td>
</tr>
<tr class="even">
<td style="text-align: left;"><p>Greydwarf</p></td>
<td style="text-align: left;"><p>Match</p></td>
<td style="text-align: left;"><p>No match</p></td>
<td style="text-align: left;"><p>No match</p></td>
<td style="text-align: left;"><p>Match</p></td>
<td style="text-align: left;"><p>No match</p></td>
</tr>
<tr class="odd">
<td style="text-align: left;"><p>Surtling</p></td>
<td style="text-align: left;"><p>No match</p></td>
<td style="text-align: left;"><p>Match</p></td>
<td style="text-align: left;"><p>No match</p></td>
<td style="text-align: left;"><p>No match</p></td>
<td style="text-align: left;"><p>No match</p></td>
</tr>
</tbody>
</table>

## Languages

<table>
<thead>
<tr class="header">
<th style="text-align: left;">Language</th>
<th style="text-align: left;">Translators</th>
<th style="text-align: left;">Status</th>
</tr>
</thead>
<tbody>
<tr class="odd">
<td style="text-align: left;"><p>English</p></td>
<td style="text-align: left;"><p>Translation Tools</p></td>
<td style="text-align: left;"><p>100%</p></td>
</tr>
<tr class="even">
<td style="text-align: left;"><p>Japanese</p></td>
<td style="text-align: left;"><p>EideeHi</p></td>
<td style="text-align: left;"><p>100%</p></td>
</tr>
</tbody>
</table>

## Credits

-   Dependencies:

    -   [Configuration
        Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager)

    -   [LitJSON](https://litjson.net)

## License

Automatics is developed and released under the MIT license. For the full
text of the license, please see the [LICENSE](https://github.com/eideehi/valheim-automatics/blob/main/LICENSE) file.

## Changelog
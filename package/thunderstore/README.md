# Automatics - Valheim Mod

Automatics is a mod that automates the tedious tasks of life in Valheim. Most of its features exist in existing mods, but it has been re-designed to make it easier for me to use.

## Features

### Automatic door

Automatically opens and closes the door near the player. The interval and distance to detect the player can be changed from the configuration.

### Automatic map pinning

Automatic pinning animals, monsters, floras, veins, dungeons, etc. that exist around the player to the map. The pinning allows for each object and the detection range of the object can be changed from the config.

**Custom map icons**

Using png files and csv, you can customize icons for pins added by
Automatic map pinning.

[![Custom Map Icons](https://app.box.com/shared/static/ggj61oyrdik1jk08lohdqr91e1q5isqv.png)](https://app.box.com/shared/static/yhdd2v0mrwzgh54tbkc7twjen17q22gn.jpg "Custom Map Icons (Click to view full size)")

-   **Files to be prepared**

    -   CustomMapIcon.csv: Describe information such as which icons to customize and which images to use. Detailed specifications are described below.

    -   ICON\_NAME.png: Image files required for each icon. A png file with transparency information is preferred.

Place the prepared files in the following configuration.

-   **/Valheim/BepInEx/plugins/Automatics/Textures**

    -   CustomMapIcon.csv

    -   ICON\_1\_NAME.png

    -   ICON\_2\_NAME.png

**Format of CustomMapIcon.csv**

Four columns are defined in this csv: TARGET, FILE, WIDTH, and HEIGHT. The first line is considered a header and is skipped, so information should be written in the second and subsequent lines.

-   TARGET: Specify the name of the object for which the custom icon is to be used. Display name (e.g. Boar) and internal name (e.g. $enemy\_boar) are available.

-   FILE: Specify the name of the image file to be used for the icon. It is best to keep the image size between 16x16 and 32x32, as too large an icon will be obtrusive.

-   WIDTH: Specify the width of the icon.

-   HEIGHT: Specify the height of the icon.

**Example files**

I don’t feel I have explained it very well, so I have prepared a sample file. Please click [here](https://github.com/eideehi/valheim-automatics/blob/main/package/extra/custom-icon-example/Automatics/Textures) to check the structure of the file that actually works. You can also download the [zip file](https://app.box.com/shared/static/n8l56o2l5or24bx1061jjly4jnm21gm3.zip) and check the operation on your PC.

-   [Example files (Github)](https://github.com/eideehi/valheim-automatics/blob/main/package/extra/custom-icon-example/Automatics/Textures)

-   [Example files (Zip)](https://app.box.com/shared/static/n8l56o2l5or24bx1061jjly4jnm21gm3.zip)

### Automatic processing

Refueling pieces that need fuel. Deliver materials to pieces that
process materials, and store items produced by pieces. These tasks can be automated via containers around the piece.

## Configurations

I recommend using [Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager). Idon’t have the energy to explain each configuration item, so I’ll upload a screenshot of the GUI. There are also tooltips to supplement the options, so please refer to those as well.

[![Configuration Menu](https://app.box.com/shared/static/3v57rjpauzzyv0xeugohnw8bn2ye3q2h.png)](https://app.box.com/shared/static/vfzsn69i950l48er2u69tssod6xxsh8u.jpg "Configuration Menu (Click to view full size)")

## Languages

<table>
<tbody>
<tr class="odd">
<td style="text-align: left;"><p>Language</p></td>
<td style="text-align: left;"><p>Translators</p></td>
<td style="text-align: left;"><p>Status</p></td>
</tr>
<tr class="even">
<td style="text-align: left;"><p>English</p></td>
<td style="text-align: left;"><p>Translation Tools</p></td>
<td style="text-align: left;"><p>100%</p></td>
</tr>
<tr class="odd">
<td style="text-align: left;"><p>Japanese</p></td>
<td style="text-align: left;"><p>EideeHi</p></td>
<td style="text-align: left;"><p>100%</p></td>
</tr>
</tbody>
</table>

## Credits

-   Dependencies:

    -   [Configuration Manager](https://github.com/BepInEx/BepInEx.ConfigurationManager)

## License

Automatics is developed and released under the MIT license. For the full text of the license, please see the [LICENSE](https://github.com/eideehi/valheim-automatics/blob/main/LICENSE) file.

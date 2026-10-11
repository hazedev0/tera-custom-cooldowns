[![Donate to Foglio](https://img.shields.io/badge/Donate%20to%20Foglio-PayPal-blue.svg)](https://paypal.me/foglio1024)

# TCC (TERA Custom Cooldowns)

TCC is a modern design replacement for some of the TERA Online UI elements.

TCC was created by [Foglio](https://github.com/Foglio1024/Tera-custom-cooldowns).
This fork is maintained by hazedev0 and continues from Foglio's last release (1.4.166) with fixes and
small changes. Donations still go to Foglio.

## Supported servers

This fork is made for and tested on **Agaia (EU, English client)**. The game data TCC downloads (skills,
abnormalities, dungeons, items and so on) comes from the
[TeraDpsMeterData fork](https://github.com/hazedev0/TeraDpsMeterData), extracted from Agaia's EU client.
On other servers or with other client languages, names and icons may be missing or wrong, and some
features may not work.

## Installing

Install TCC as a TERA Toolbox module:

1. In your Toolbox folder, create `mods\TCC`. If you already have Foglio's TCC there, keep the folder:
   your settings stay.
2. Save [module.json](https://raw.githubusercontent.com/hazedev0/tcc-toolbox-release/master/module.json)
   into it, replacing the old one.
3. Start Toolbox. It downloads TCC (and the `external-interface` module it needs) and keeps it updated
   from this fork.

This fork only supports Toolbox. To run TCC without Toolbox (standalone), use
[Foglio's version](https://github.com/Foglio1024/Tera-custom-cooldowns/releases).

The [overview](https://github.com/Foglio1024/Tera-custom-cooldowns/wiki/TCC-windows-overview) and the
[wiki](https://github.com/Foglio1024/Tera-custom-cooldowns/wiki), written by Foglio for the original TCC,
explain the windows and settings.

## Bugs and help

Open an issue at https://github.com/hazedev0/tera-custom-cooldowns/issues. Include your TCC version and,
if there is one, `mods\TCC\logs\error.log`.

## License

MIT, see [LICENSE](LICENSE). TCC is Foglio's work; this fork's changes are under the same license.

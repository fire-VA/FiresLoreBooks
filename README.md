# FiresLoreBooks

Lore books you can place, read, collect and edit, and journals you write yourself.

Admins write the world's lore; players find it on shelves and lecterns, read it, and keep a copy in their own
collection. Players also get a journal of their own for notes and pictures, which they can share or hand on.

## What's new in 0.0.19

- **Quicker quitting.** The game closes faster with this mod installed: it no longer spends time undoing its own
  changes while the game is shutting down.

## Features

- **Placed lore books** (Hammer, Furniture category). Tap E to read, hold E to take a copy. Each player can take
  one copy per respawn; the book stays in place for everyone else.
- **Your collection.** Every book you have taken is kept in your lore collection, with a count of how many books
  exist.
- **Reader.** Turn pages with A / D, the arrow keys, the mouse wheel or the page corners. Books can carry
  formatting, colour, emoji and pictures.
- **Book editor** (admins). Write and edit books in game: title, cover colour, pages, pictures and read rewards.
- **Server text files.** Books live on the server in `BepInEx/config/FiresLoreBooks/Books/<id>.txt` and can be
  edited outside the game; open readers refresh by themselves. An example book is created for you.
- **Read rewards.** Reading a book to its last page can grant quest steps, achievements, class XP, skills or
  attribute points, once per player. These need FiresRPGmaker / FiresRPGClasses installed.
- **Journal.** Craft one at the workbench. Write entries, take pictures with the journal's camera, lock it so only
  you can write, or give it to a friend. A journal lectern lets others read a journal you place on it.

## Requires

- BepInExPack for Valheim
- FiresUnifiedCore 0.2.1223 or newer
- Optional: FiresRPGmaker / FiresRPGClasses for read rewards

## Installation

- Install with a mod manager, or copy the `FiresLoreBooks` folder into `BepInEx/plugins`.
- Install it on every client and on the dedicated server. The server keeps the books, the journals and who has
  taken what.

## Configuration

Settings are in `BepInEx/config/com.Fire.FiresLoreBooks.cfg` (or the Fires config window, F1).

- `[Books] Respawn after taking` (default `4d`) - how long until a player can take another copy of the same placed
  book: `4d` game days, `12h` game hours, `30m` game minutes, or `never`. This is a server setting. A book's own
  `# respawn:` line wins over it.
- `[Pictures] Shoot key` (default Space) and `Cancel key` (default right mouse button) for the journal camera.
  Left click always takes the picture and Esc always cancels.
- `[Pictures] Picture size` (default 512, 256 to 1024) - pixels on a picture's long side.

## Book files

A book file is plain text. Header lines at the top set its details, for example:

```
# title: The Fall of the Elder
# cover: #5a3a1e
# respawn: 4d
# hook: GiveClassXP 200 once
```

`# hook:` lines are the read rewards, one per line.

## Console commands

| command | what it does |
|---|---|
| `lorebook_open [id]` | open a lore book in the reader |
| `lorebook_close` | close the reader or editor |
| `lorebook_collection` | your lore collection |
| `lorebook_edit [id]` | edit or create a lore book (admin) |
| `lorebook_list` | list the server's lore books (server or host only) |

## Multiplayer

- Only admins can create or edit lore books.
- A journal belongs to its creator until they give it away. The owner and admins can always write in it; when it
  is unlocked, other players can add entries but cannot change older ones.

## Credits

- Emoji graphics: Twemoji by Twitter, Inc. and other contributors (jdecked/twemoji, v17.0.3), licensed CC-BY 4.0:
  https://creativecommons.org/licenses/by/4.0/ - see THIRDPARTY-NOTICES.md.
- Book models: a licensed Unity Asset Store pack (used with permission of the purchaser).

## Licence

MIT - see `LICENSE`.

## Issues / questions

https://discord.gg/zQRgHbqms4

## Screenshots

<!-- Image hosting is Fire's call (morning 10-20). Replace HOSTED_URL with the image host and uncomment each line. Files: Tools\readme_shots\20261020\FiresLoreBooks\ -->
<!-- ![A journal page with a picture](HOSTED_URL/FiresLoreBooks/01_journal_open.png) -->
<!-- ![Editing a page](HOSTED_URL/FiresLoreBooks/02_journal_edit.png) -->
<!-- ![A lore book in the world](HOSTED_URL/FiresLoreBooks/03_book_world.png) -->

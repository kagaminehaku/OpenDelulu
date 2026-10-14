# Translating Open Delulu

The interface text lives in one JSON file per language in [`OpenDelulu/Assets/Lang/`](../OpenDelulu/Assets/Lang/):

| File | Language |
|---|---|
| `en.json` | English (source: every other file is translated from it) |
| `vi.json` | Tiếng Việt |

Each file is a flat object of `"key": "text"` pairs:

```json
{
  "_language": "Tiếng Việt",
  "play.enter": "Vào game",
  "play.nextLevel": "Tiến tới thân mật {0}"
}
```

## Adding or improving a language

1. Copy `en.json` to `<code>.json`, where `<code>` is the language's
   [BCP 47 code](https://en.wikipedia.org/wiki/IETF_language_tag): `de`, `fr`, `ja`, `pt-BR`, `zh-Hans`…
2. Set `"_language"` to the language's own name ("Deutsch", "日本語"…). It is what the language menu shows.
3. Translate the values. **Do not change the keys.**
4. Keep every placeholder: `{0}`, `{1}`… are filled in by the app (names, numbers, file names). Their order in the
   sentence may change, and number formats such as `{2:N0}` must keep the part after the colon.
5. Check the file:

   ```
   dotnet run --project OpenDelulu.Cli -- lang-check OpenDelulu/Assets/Lang/<code>.json
   ```

   It lists missing, empty and unknown keys and placeholders that differ from English.
6. Try it without rebuilding: put the file in a `lang` folder next to the executable, or in
   `%LOCALAPPDATA%\OpenDelulu\lang` (Windows) / `~/.local/share/OpenDelulu/lang` (Linux); the language then appears
   in the menu at the top right. A file there also overrides the shipped one with the same code, which is handy
   for fixing a translation.
7. Open a pull request that adds or changes the file in `OpenDelulu/Assets/Lang/`.

Untranslated keys fall back to English, so a partial translation is fine to share.

## Notes

- Keys are grouped by screen: `top.*` (folder bar), `tab.*`, `play.*`, `char.*` (Characters tab), `scripts.*`,
  `data.*`, `preview.*`, `audio.*`, `gfx.*` (graphics errors), `title.*` and `speaker.*` (game script titles and
  names), `time.*`, `backend.*`, `status.*`, `hint.*`.
- Game dialogue itself is not part of these files: it comes from the installed game's scripts.
- Keep the files UTF-8. Comments (`//`) and trailing commas are tolerated by the app but avoid them in pull requests.

## For developers

- XAML: `Text="{l:Tr play.enter}"` (namespace `xmlns:l="using:OpenDelulu.Localization"`).
- Code: `Translator.Get("key")`, `Translator.Format("key", args)`; in view models `T("key")` and `F("key", args)`.
  Text built in code should be refreshed in `OnLanguageChanged()`; list entries use `LocalizedText`.
- Add every new key to `en.json` (and, if you can, the other files), then run `lang-check` on them.

# Shop payload canonicalization regression

This console fixture calls the production `ShopService.CloneEntry` and needs the actual game assemblies plus a built NoSuffering DLL. It checks formatting equivalence and retained gameplay differences without starting a game.

```sh
dotnet run --project tests/shop-payload/ShopPayload.csproj -c Release \
  -p:ModAssembly=/absolute/path/NoSuffering.dll \
  -p:Sts2AssemblyDir=/absolute/path/game/assembly/directory \
  -- /absolute/path/mac-capture.json /absolute/path/windows-capture.json
```

The two capture arguments are optional. When supplied, every actor's complete record is compared after production canonicalization. The synthetic cases always check LF/CRLF, nested object-key order, upgrades, prices, sale status, array order, card pools, saved slot templates and unchanged source records.

Pass `--native /absolute/path/mac-native-digest.json /absolute/path/windows-native-digest.json` instead to exercise the production native digest preparation against paired purchase captures. The captured remote-only merchant picked-card annotation must converge, while gold, deck, `CardsGained` and picked choices in a non-shop room must still differ. Input files stay unchanged.

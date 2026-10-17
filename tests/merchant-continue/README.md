# Merchant native continuation regression

This console fixture calls the production load and entry-hook prefixes against
the actual game assemblies. It verifies that only pending stock at the paired
location supplies a merchant to native Continue or committed snapshot loading,
that a serialized room is preserved, and that ordinary entry and pre-entry
rollback retain native behavior. The native load task remains in charge.

```sh
DOTNET_CLI_HOME="$PWD/.tools/dotnet-home" .tools/dotnet/dotnet run \
  --project tests/merchant-continue/MerchantContinue.csproj -c Release \
  -p:ModAssembly=/absolute/path/NoSuffering.dll \
  -p:Sts2AssemblyDir=/absolute/path/game/assembly/directory
```

Run separately with matching public-beta and public builds. The fixture isolates
the current shop context from Godot and does not launch a game; multiplayer UI,
transport and saved-floor verification use the game regression procedure.

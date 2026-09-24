#!/bin/sh

# The .NET 8 tool needs to run on .NET 10 to discover this project's SDK.
DOTNET_ROLL_FORWARD=LatestMajor exec "$HOME/.dotnet/tools/dotnet-unused" "$@"

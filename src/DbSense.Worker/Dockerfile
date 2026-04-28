FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/DbSense.Contracts/ src/DbSense.Contracts/
COPY src/DbSense.Core/ src/DbSense.Core/
COPY src/DbSense.Worker/ src/DbSense.Worker/

RUN dotnet restore src/DbSense.Worker/DbSense.Worker.csproj
RUN dotnet publish src/DbSense.Worker/DbSense.Worker.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
EXPOSE 5001
ENTRYPOINT ["dotnet", "DbSense.Worker.dll"]

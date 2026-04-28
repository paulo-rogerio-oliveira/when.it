FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/DbSense.Contracts/ src/DbSense.Contracts/
COPY src/DbSense.Core/ src/DbSense.Core/
COPY src/DbSense.Api/ src/DbSense.Api/

RUN dotnet restore src/DbSense.Api/DbSense.Api.csproj
RUN dotnet publish src/DbSense.Api/DbSense.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["dotnet", "DbSense.Api.dll"]

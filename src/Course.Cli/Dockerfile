FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Course.Shared/Course.Shared.csproj src/Course.Shared/
COPY src/Course.Cli/Course.Cli.csproj src/Course.Cli/
RUN dotnet restore src/Course.Cli/Course.Cli.csproj
COPY src/Course.Shared/ src/Course.Shared/
COPY src/Course.Cli/ src/Course.Cli/
RUN dotnet publish src/Course.Cli/Course.Cli.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./
COPY migrations/ ./migrations/
COPY maps/ ./maps/
ENTRYPOINT ["dotnet", "Course.Cli.dll"]

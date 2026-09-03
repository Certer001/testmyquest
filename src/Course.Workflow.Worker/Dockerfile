FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Course.sln ./
COPY src/Course.Shared/Course.Shared.csproj src/Course.Shared/
COPY src/Course.Workflow.Worker/Course.Workflow.Worker.csproj src/Course.Workflow.Worker/
RUN dotnet restore src/Course.Workflow.Worker/Course.Workflow.Worker.csproj
COPY src/Course.Shared/ src/Course.Shared/
COPY src/Course.Workflow.Worker/ src/Course.Workflow.Worker/
RUN dotnet publish src/Course.Workflow.Worker/Course.Workflow.Worker.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "Course.Workflow.Worker.dll"]

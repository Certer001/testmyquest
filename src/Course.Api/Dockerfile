FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Course.sln ./
COPY src/Course.Shared/Course.Shared.csproj src/Course.Shared/
COPY src/Course.Api/Course.Api.csproj src/Course.Api/
RUN dotnet restore src/Course.Api/Course.Api.csproj
COPY src/Course.Shared/ src/Course.Shared/
COPY src/Course.Api/ src/Course.Api/
COPY migrations/ migrations/
RUN dotnet publish src/Course.Api/Course.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./
COPY migrations/ ./migrations/
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Course.Api.dll"]

# === Сборка ===
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app/publish

# === Runtime ===
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
# Файл задач по умолчанию — в корне приложения (как и локально);
# в docker-compose переопределяется на /data/tasks.json с bind-mount.
EXPOSE 8080
ENTRYPOINT ["dotnet", "PersonalDashboard.dll"]
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY KnowledgeBase.Api/KnowledgeBase.Api.csproj KnowledgeBase.Api/
RUN dotnet restore KnowledgeBase.Api/KnowledgeBase.Api.csproj
COPY KnowledgeBase.Api/ KnowledgeBase.Api/
RUN dotnet publish KnowledgeBase.Api/KnowledgeBase.Api.csproj -c Release -o /app/publish --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8081
COPY --from=build /app/publish .
EXPOSE 8081
ENTRYPOINT ["dotnet", "KnowledgeBase.Api.dll"]

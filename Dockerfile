# force rebuild
# force rebuild 3
# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["AiBackend.csproj", "./"]
RUN dotnet restore "./AiBackend.csproj"

COPY . .
RUN dotnet publish "AiBackend.csproj" -c Release -o /app/publish --no-restore

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

EXPOSE 8080
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "AiBackend.dll"]

# absolute clean build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# ✅ Fix: Npgsql / GSSAPI için gerekli Linux lib
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "AiBackend.dll"]

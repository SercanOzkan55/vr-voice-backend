# --- build ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

# --- runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# libgssapi_krb5.so.2 -> libkrb5 paketiyle gelir (Debian tabanlı imajlarda)
RUN apt-get update \
    && apt-get install -y --no-install-recommends libkrb5-3 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AiBackend.dll"]

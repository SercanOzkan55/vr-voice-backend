# --- Build Aşaması ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Proje dosyalarını kopyala
COPY . .

# Release modunda derle ve /app/publish klasörüne çıkar
RUN dotnet publish -c Release -o /app/publish

# --- Runtime (Çalışma) Aşaması ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# SQL Client için gerekli Linux kütüphanesini yükle (Hata düzeltici kısım)
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

# Derlenen dosyaları build aşamasından al
COPY --from=build /app/publish .

# Portu dışarı aç
EXPOSE 8080

# Uygulamayı başlat (Senin DLL ismin AiBackend.dll görünüyor)
ENTRYPOINT ["dotnet", "AiBackend.dll"]
# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Sadece projeyi kopyalayıp restore yapmak cache avantajı sağlar
COPY ["AiBackend.csproj", "./"]
RUN dotnet restore "./AiBackend.csproj"

# Kalan tüm dosyaları kopyala ve derle
COPY . .
RUN dotnet publish "AiBackend.csproj" -c Release -o /app/publish --no-restore

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Railway/Render gibi platformlar için port ayarı
ENV ASPNETCORE_URLS=http://*:8080
EXPOSE 8080

# Build aşamasından dosyaları tam yolunda al
COPY --from=build /app/publish .

# DLL adının büyük/küçük harf duyarlılığına dikkat (AiBackend.dll)
ENTRYPOINT ["dotnet", "AiBackend.dll"]
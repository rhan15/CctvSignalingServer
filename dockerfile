# --- STAGE 1: Build ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy file project dan restore dependencies
COPY ["NamaProjectAnda.csproj", "./"]
RUN dotnet restore "NamaProjectAnda.csproj"

# Copy seluruh source code
COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# --- STAGE 2: Runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
EXPOSE 5000

# Mengatur agar aplikasi listen ke semua interface di port 5000 dalam container
ENV ASPNETCORE_URLS=http://+:5000

# Copy hasil publish dari stage build
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "NamaProjectAnda.dll"]
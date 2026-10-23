# --- STAGE 1: Build ---
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy file project dan restore dependencies
COPY ["CctvSignalingServer.csproj", "./"]
RUN dotnet restore "CctvSignalingServer.csproj"

# Copy seluruh source code
COPY . .
# Tambahkan --verbosity normal di sini
RUN dotnet publish "CctvSignalingServer.csproj" -c Release -o /app/publish /p:UseAppHost=false --verbosity normal

# --- STAGE 2: Runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:5000
ENV TZ=Asia/Jakarta

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "CctvSignalingServer.dll"]
FROM mcr.microsoft.com/dotnet/sdk:10.0.301@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 AS build
WORKDIR /src
COPY Ol.Ci.NuGet.csproj ./
RUN dotnet restore Ol.Ci.NuGet.csproj --packages /nuget/packages
RUN dotnet build Ol.Ci.NuGet.csproj --configuration Release --no-restore

FROM scratch AS export
COPY --from=build /src/obj/project.assets.json /obj/project.assets.json
COPY --from=build /src/bin/Release/net10.0/Ol.Ci.NuGet.deps.json /bin/Release/net10.0/Ol.Ci.NuGet.deps.json

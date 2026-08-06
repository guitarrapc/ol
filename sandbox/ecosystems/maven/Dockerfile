FROM maven:3.9-eclipse-temurin-21@sha256:c07f7ccfb8ca6c9fa29ee523f00afa7d2ca6132c92f8652c4aebb5ee3491f502 AS generate
WORKDIR /src
COPY pom.xml ./
RUN mvn --batch-mode org.apache.maven.plugins:maven-dependency-plugin:3.11.0:tree -DoutputType=json -DoutputFile=target/maven-dependency-tree.json

FROM scratch AS export
COPY --from=generate /src/target/maven-dependency-tree.json /target/maven-dependency-tree.json

namespace PinqOps.Web;

/// <summary>One installable catalog app: everything needed for a docker run.</summary>
public sealed record AppSpec(
    string Id,
    string Name,
    string Category,
    string Image,
    (int Host, int Container)[] Ports,
    string[] Env,
    (string Volume, string Path)[] Volumes,
    string? Cmd = null,
    string? Note = null);

/// <summary>
/// Curated one-click catalog of popular self-hosted services. Every entry is a
/// fixed, reviewed spec — the install endpoint only accepts catalog ids, so
/// the API can never be used to run an arbitrary image. Containers are named
/// pinqops-&lt;id&gt; and labeled pinqops.app=&lt;id&gt; so the dashboard can
/// track them. Default credentials (change them!) are noted per app.
/// </summary>
public static class AppCatalog
{
    public const string ContainerPrefix = "pinqops-";
    public const string Label = "pinqops.app";

    /// <summary>
    /// All catalog apps join this user-defined network so they can reach each
    /// other by container name (the default bridge has no name DNS).
    /// </summary>
    public const string SharedNetwork = "pinqops-apps";

    public static readonly IReadOnlyList<AppSpec> Apps =
    [
        // --- Databases & caches ---
        new("redis", "Redis", "database", "redis:7-alpine", [(6379, 6379)], [], [("data", "/data")]),
        new("keydb", "KeyDB", "database", "eqalpha/keydb:latest", [(6380, 6379)], [], [("data", "/data")]),
        new("memcached", "Memcached", "database", "memcached:alpine", [(11211, 11211)], [], []),
        new("postgres", "PostgreSQL", "database", "postgres:16-alpine", [(5432, 5432)], ["POSTGRES_PASSWORD=pinqops"], [("data", "/var/lib/postgresql/data")], Note: "user: postgres / pinqops"),
        new("mysql", "MySQL", "database", "mysql:8", [(3306, 3306)], ["MYSQL_ROOT_PASSWORD=pinqops"], [("data", "/var/lib/mysql")], Note: "root / pinqops"),
        new("mariadb", "MariaDB", "database", "mariadb:11", [(3307, 3306)], ["MARIADB_ROOT_PASSWORD=pinqops"], [("data", "/var/lib/mysql")], Note: "root / pinqops"),
        new("mongo", "MongoDB", "database", "mongo:7", [(27017, 27017)], [], [("data", "/data/db")]),
        new("couchdb", "CouchDB", "database", "couchdb:3", [(5984, 5984)], ["COUCHDB_USER=admin", "COUCHDB_PASSWORD=pinqops"], [("data", "/opt/couchdb/data")], Note: "admin / pinqops"),
        new("neo4j", "Neo4j", "database", "neo4j:5", [(7474, 7474), (7687, 7687)], ["NEO4J_AUTH=neo4j/pinqops123"], [("data", "/data")], Note: "neo4j / pinqops123"),
        new("clickhouse", "ClickHouse", "database", "clickhouse/clickhouse-server:latest", [(8123, 8123)], [], [("data", "/var/lib/clickhouse")]),
        new("influxdb", "InfluxDB", "database", "influxdb:2", [(8086, 8086)],
            ["DOCKER_INFLUXDB_INIT_MODE=setup", "DOCKER_INFLUXDB_INIT_USERNAME=admin", "DOCKER_INFLUXDB_INIT_PASSWORD=pinqops123", "DOCKER_INFLUXDB_INIT_ORG=pinqops", "DOCKER_INFLUXDB_INIT_BUCKET=default"],
            [("data", "/var/lib/influxdb2")], Note: "admin / pinqops123"),
        new("questdb", "QuestDB", "database", "questdb/questdb:latest", [(9002, 9000)], [], [("data", "/var/lib/questdb")]),
        new("cassandra", "Cassandra", "database", "cassandra:5", [(9042, 9042)], [], [("data", "/var/lib/cassandra")]),
        new("cockroachdb", "CockroachDB", "database", "cockroachdb/cockroach:latest", [(26257, 26257), (8081, 8080)], [], [("data", "/cockroach/cockroach-data")], Cmd: "start-single-node --insecure"),
        new("surrealdb", "SurrealDB", "database", "surrealdb/surrealdb:latest", [(8010, 8000)], [], [], Cmd: "start"),

        // --- Search, queues & messaging ---
        new("elasticsearch", "Elasticsearch", "search-queue", "docker.elastic.co/elasticsearch/elasticsearch:8.17.0", [(9200, 9200)],
            ["discovery.type=single-node", "xpack.security.enabled=false", "ES_JAVA_OPTS=-Xms512m -Xmx512m"], [("data", "/usr/share/elasticsearch/data")]),
        new("opensearch", "OpenSearch", "search-queue", "opensearchproject/opensearch:2", [(9201, 9200)],
            ["discovery.type=single-node", "DISABLE_SECURITY_PLUGIN=true"], [("data", "/usr/share/opensearch/data")]),
        new("meilisearch", "Meilisearch", "search-queue", "getmeili/meilisearch:latest", [(7700, 7700)], ["MEILI_MASTER_KEY=pinqops-master-key"], [("data", "/meili_data")], Note: "key: pinqops-master-key"),
        new("typesense", "Typesense", "search-queue", "typesense/typesense:27.1", [(8108, 8108)], ["TYPESENSE_API_KEY=pinqops", "TYPESENSE_DATA_DIR=/data"], [("data", "/data")], Note: "key: pinqops"),
        new("rabbitmq", "RabbitMQ", "search-queue", "rabbitmq:3-management", [(5672, 5672), (15672, 15672)], [], [("data", "/var/lib/rabbitmq")], Note: "guest / guest (ui: 15672)"),
        new("nats", "NATS", "search-queue", "nats:latest", [(4222, 4222), (8222, 8222)], [], []),
        new("kafka", "Apache Kafka", "search-queue", "apache/kafka:latest", [(9092, 9092)], [], []),
        new("mosquitto", "Mosquitto MQTT", "search-queue", "eclipse-mosquitto:2", [(1883, 1883)], [], [("data", "/mosquitto/data")], Cmd: "mosquitto -c /mosquitto-no-auth.conf"),

        // --- Storage & web servers ---
        new("minio", "MinIO", "web-storage", "minio/minio:latest", [(9000, 9000), (9001, 9001)],
            ["MINIO_ROOT_USER=pinqops", "MINIO_ROOT_PASSWORD=pinqops123"], [("data", "/data")], Cmd: "server /data --console-address :9001", Note: "pinqops / pinqops123 (console: 9001)"),
        new("nginx", "Nginx", "web-storage", "nginx:alpine", [(8090, 80)], [], []),
        new("caddy", "Caddy", "web-storage", "caddy:2", [(8091, 80)], [], [("data", "/data")]),
        new("httpd", "Apache httpd", "web-storage", "httpd:alpine", [(8092, 80)], [], []),

        // --- DB admin tools ---
        new("adminer", "Adminer", "admin-tool", "adminer:latest", [(8083, 8080)], [], []),
        new("pgadmin", "pgAdmin", "admin-tool", "dpage/pgadmin4:latest", [(5050, 80)],
            ["PGADMIN_DEFAULT_EMAIL=admin@pinqops.local", "PGADMIN_DEFAULT_PASSWORD=pinqops"], [("data", "/var/lib/pgadmin")], Note: "admin@pinqops.local / pinqops"),
        new("phpmyadmin", "phpMyAdmin", "admin-tool", "phpmyadmin:latest", [(8082, 80)], ["PMA_ARBITRARY=1"], []),
        new("mongo-express", "Mongo Express", "admin-tool", "mongo-express:latest", [(8093, 8081)],
            ["ME_CONFIG_MONGODB_URL=mongodb://pinqops-mongo:27017"], [], Note: "install the MongoDB app too"),
        new("redisinsight", "RedisInsight", "admin-tool", "redis/redisinsight:latest", [(5540, 5540)], [], [("data", "/data")]),

        // --- Monitoring ---
        new("grafana", "Grafana", "monitoring", "grafana/grafana:latest", [(3000, 3000)], [], [("data", "/var/lib/grafana")], Note: "admin / admin"),
        new("prometheus", "Prometheus", "monitoring", "prom/prometheus:latest", [(9090, 9090)], [], [("data", "/prometheus")]),
        new("uptime-kuma", "Uptime Kuma", "monitoring", "louislam/uptime-kuma:1", [(3001, 3001)], [], [("data", "/app/data")]),
        new("netdata", "Netdata", "monitoring", "netdata/netdata:latest", [(19999, 19999)], [], []),

        // --- Dev & CI ---
        new("gitea", "Gitea", "dev-ci", "gitea/gitea:latest", [(3002, 3000), (2222, 22)], [], [("data", "/data")]),
        new("jenkins", "Jenkins", "dev-ci", "jenkins/jenkins:lts", [(8084, 8080)], [], [("data", "/var/jenkins_home")]),
        new("code-server", "code-server", "dev-ci", "codercom/code-server:latest", [(8085, 8080)], ["PASSWORD=pinqops"], [("data", "/home/coder")], Note: "password: pinqops"),
        new("verdaccio", "Verdaccio", "dev-ci", "verdaccio/verdaccio:latest", [(4873, 4873)], [], [("data", "/verdaccio/storage")]),
        new("sonarqube", "SonarQube", "dev-ci", "sonarqube:community", [(9003, 9000)], [], [("data", "/opt/sonarqube/data")], Note: "admin / admin"),

        // --- Auth & security ---
        new("keycloak", "Keycloak", "auth", "quay.io/keycloak/keycloak:latest", [(8880, 8080)],
            ["KEYCLOAK_ADMIN=admin", "KEYCLOAK_ADMIN_PASSWORD=pinqops"], [], Cmd: "start-dev", Note: "admin / pinqops"),
        new("vaultwarden", "Vaultwarden", "auth", "vaultwarden/server:latest", [(8087, 80)], [], [("data", "/data")]),

        // --- Applications ---
        new("wordpress", "WordPress", "app", "wordpress:latest", [(8088, 80)],
            ["WORDPRESS_DB_HOST=pinqops-mysql", "WORDPRESS_DB_USER=root", "WORDPRESS_DB_PASSWORD=pinqops", "WORDPRESS_DB_NAME=wordpress"],
            [("data", "/var/www/html")], Note: "install the MySQL app too"),
        new("ghost", "Ghost", "app", "ghost:5-alpine", [(2368, 2368)],
            ["NODE_ENV=development", "url=http://localhost:2368"], [("data", "/var/lib/ghost/content")]),
        new("nextcloud", "Nextcloud", "app", "nextcloud:apache", [(8089, 80)], [], [("data", "/var/www/html")]),
        new("jellyfin", "Jellyfin", "app", "jellyfin/jellyfin:latest", [(8096, 8096)], [], [("config", "/config"), ("cache", "/cache")]),
        new("navidrome", "Navidrome", "app", "deluan/navidrome:latest", [(4533, 4533)], [], [("data", "/data")]),
        new("syncthing", "Syncthing", "app", "syncthing/syncthing:latest", [(8384, 8384)], [], [("data", "/var/syncthing")]),
        new("n8n", "n8n", "app", "n8nio/n8n:latest", [(5678, 5678)], ["N8N_SECURE_COOKIE=false"], [("data", "/home/node/.n8n")]),
        new("nodered", "Node-RED", "app", "nodered/node-red:latest", [(1880, 1880)], [], [("data", "/data")]),
    ];

    public static AppSpec? Find(string id) =>
        Apps.FirstOrDefault(app => string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase));
}

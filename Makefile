NATIVE_RIDS := linux-x64 linux-arm64 osx-arm64 win-x64
OUTPUT ?= dist
override COVERAGE_DIR := artifacts/coverage
override COVERAGE_REPORT := $(COVERAGE_DIR)/coverage.cobertura.xml
override COVERAGE_RAW := $(COVERAGE_DIR)/raw.cobertura.xml
override SONAR_DIR := artifacts/sonar

.PHONY: tool-restore restore format build coverage coverage-check analysis-config-check badge-check security-exceptions security sonar-prepare python-tests js-tests test publish package-native audit backup image image-scan helm-lint helm-template release-check verify-fast verify

tool-restore:
	dotnet tool restore

restore:
	dotnet restore PerfSentinelHub.sln --locked-mode

format: restore
	dotnet format PerfSentinelHub.sln --verify-no-changes --no-restore

build: restore
	dotnet build PerfSentinelHub.sln -c Release --no-restore --warnaserror

coverage: tool-restore build
	rm -rf "$(COVERAGE_DIR)" TestResults
	mkdir -p "$(COVERAGE_DIR)"
	dotnet test PerfSentinelHub.sln -c Release --no-build --no-restore -- --coverage --coverage-output-format cobertura --coverage-output "$(CURDIR)/$(COVERAGE_RAW)" --report-trx --report-trx-filename tests.trx
	@test -f "$(COVERAGE_RAW)" || { echo "expected a Cobertura report at $(COVERAGE_RAW)" >&2; exit 1; }
	dotnet tool run reportgenerator -- -reports:"$(COVERAGE_RAW)" -targetdir:"$(COVERAGE_DIR)" -reporttypes:Cobertura "-filefilters:-**/obj/**"
	python3 scripts/normalize-coverage.py "$(COVERAGE_DIR)/Cobertura.xml" "$(COVERAGE_REPORT)"
	rm -f "$(COVERAGE_DIR)/Cobertura.xml" "$(COVERAGE_RAW)"
	mv TestResults/tests.trx "$(COVERAGE_DIR)/tests.trx"

coverage-check: coverage
	python3 scripts/check-coverage.py --current-report "$(COVERAGE_REPORT)"

analysis-config-check:
	python3 scripts/check-analysis-config.py

badge-check:
	python3 scripts/check-badges.py

security-exceptions:
	python3 scripts/check-security-exceptions.py

security: security-exceptions analysis-config-check audit
	python3 scripts/check-supply-chain.py

sonar-prepare: analysis-config-check tool-restore coverage
	rm -rf "$(SONAR_DIR)"
	dotnet tool run reportgenerator -- -reports:"$(COVERAGE_REPORT)" -targetdir:"$(SONAR_DIR)" -reporttypes:SonarQube
	python3 scripts/check-analysis-config.py --require-analysis-inputs

python-tests:
	python3 -m unittest discover -s scripts/tests

# The launcher's command builders and its text tables, then the same builders
# against the real engine. Node's own runner, no package and no build step, the
# way launcher.js itself is authored. The last file skips itself when there is
# no engine build to run, so this target needs nothing that the others did not.
js-tests:
	node --test tests/launcher.test.js tests/text.test.js tests/launcher-e2e.test.js

test: coverage

publish: restore
	dotnet publish PerfSentinelHub/PerfSentinelHub.csproj -c Release -r linux-$${TARGETARCH:-arm64} --self-contained true -p:PublishAot=true --no-restore

package-native:
	@case " $(NATIVE_RIDS) " in *" $(RID) "*) ;; *) echo "RID must be one of $(NATIVE_RIDS)" >&2; exit 2;; esac
	@test -n "$(VERSION)" || { echo "VERSION is required" >&2; exit 2; }
	@test -n "$(COMMIT_TIME)" || { echo "COMMIT_TIME is required" >&2; exit 2; }
	dotnet restore PerfSentinelHub/PerfSentinelHub.csproj --locked-mode
	dotnet publish PerfSentinelHub/PerfSentinelHub.csproj -c Release -r "$(RID)" --self-contained true -p:PublishAot=true -p:Version="$(VERSION)" --no-restore
	python3 scripts/package-native.py --rid "$(RID)" --version "$(VERSION)" --commit-time "$(COMMIT_TIME)" --input "PerfSentinelHub/bin/Release/net10.0/$(RID)/publish" --output "$(OUTPUT)"

audit: restore
	dotnet package list --project PerfSentinelHub.sln --vulnerable --include-transitive --format json --no-restore > /tmp/perf-sentinel-hub-vulnerabilities.json
	python3 -c 'import json,sys; d=json.load(open("/tmp/perf-sentinel-hub-vulnerabilities.json")); v=[x for p in d.get("projects",[]) for f in p.get("frameworks",[]) for k in ("topLevelPackages","transitivePackages") for x in f.get(k,[]) if x.get("vulnerabilities")]; sys.exit(bool(v))'

backup: build
	@test -n "$(DEST)" || { echo "DEST is required (backup file to create)" >&2; exit 2; }
	Hub__DatabasePath="$${DB:-/data/hub.db}" dotnet run --project PerfSentinelHub/PerfSentinelHub.csproj -c Release --no-build --no-restore -- backup "$(DEST)"

image:
	docker build --platform linux/$${TARGETARCH:-arm64} -t perf-sentinel-hub:$${TAG:-local} .

image-scan: image
	trivy image --exit-code 1 --ignore-unfixed --severity HIGH,CRITICAL perf-sentinel-hub:$${TAG:-local}

# These targets exercise the templates, not an image, so any digest that is
# not the unstamped placeholder will do.
SYNTHETIC_DIGEST := sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa

HELM_TEST_VALUES := --set 'sources[0].id=test' --set 'sources[0].name=test' --set 'sources[0].environment=test' --set 'sources[0].baseUrl=http://perf-sentinel:4318' --set image.digest=$(SYNTHETIC_DIGEST)

HELM_AUTH_VALUES := --set hub.auth.enabled=true --set hub.auth.authorizationEndpoint=https://sso.example.com/auth --set hub.auth.tokenEndpoint=https://sso.example.com/token --set hub.auth.userInformationEndpoint=https://sso.example.com/userinfo --set hub.auth.clientId=hub --set hub.auth.clientSecretName=hub-oauth --set hub.auth.clientSecretKey=client-secret

# One entry of each hub.access list, access off unless HELM_ACCESS_ON follows.
HELM_ACCESS_VALUES := --set 'hub.access.adminGroups={/hub-admins}' --set 'hub.access.groups[0].name=ci-shop' --set 'hub.access.projects[0].name=shop' --set 'hub.access.projects[0].patterns[0].attribute=team=shop' --set 'hub.access.roles[0].name=ci-reader' --set 'hub.access.roles[0].permissions={findings.read}' --set 'hub.access.grants[0].group=internal:ci-shop' --set 'hub.access.grants[0].project=shop' --set 'hub.access.grants[0].role=ci-reader' --set 'hub.access.machineTokens[0].name=ci-shop' --set 'hub.access.machineTokens[0].group=internal:ci-shop' --set-string 'hub.access.machineTokens[0].expiresAt=2027-06-30T00:00:00Z' --set 'hub.access.machineTokens[0].secretName=hub-machine-tokens' --set 'hub.access.machineTokens[0].secretKey=ci-shop'

# Access on, with the metrics port it requires; the fixture above provides the admin.
HELM_ACCESS_ON := --set hub.access.enabled=true --set hub.metricsPort=9464

# A local account in a group of its own; access on needs trustedProxyNetworks beside it.
HELM_LOCAL_ACCOUNT := --set 'hub.access.localAccounts[0].name=ops' --set 'hub.access.localAccounts[0].secretName=hub-break-glass' --set 'hub.access.localAccounts[0].secretKey=ops' --set 'hub.access.groups[1].name=break-glass' --set 'hub.access.groups[1].members={local:ops}'

HELM_SECOND_TOKEN := --set 'hub.access.machineTokens[1].group=internal:ci-shop' --set-string 'hub.access.machineTokens[1].expiresAt=2027-06-30T00:00:00Z' --set 'hub.access.machineTokens[1].secretName=hub-machine-tokens' --set 'hub.access.machineTokens[1].secretKey=ci-other'

# A recap subscription delivering by webhook and by e-mail through an authenticated relay.
HELM_RECAP_VALUES := --set 'hub.recaps.webhooks[0].name=team' --set 'hub.recaps.webhooks[0].secretName=hub-recaps' --set 'hub.recaps.webhooks[0].urlKey=team-url' --set 'hub.recaps.webhooks[0].secretKey=team-secret' --set hub.recaps.smtp.host=smtp.example.com --set hub.recaps.smtp.from=hub@example.com --set hub.recaps.smtp.userName=hub --set hub.recaps.smtp.passwordSecretName=hub-recaps --set hub.recaps.smtp.passwordSecretKey=smtp-password --set 'hub.recaps.subscriptions[0].name=shop' --set 'hub.recaps.subscriptions[0].cadences={week}' --set 'hub.recaps.subscriptions[0].views={pilotage}' --set 'hub.recaps.subscriptions[0].webhooks={team}' --set 'hub.recaps.subscriptions[0].emails={lead@example.com}'

helm-lint:
	helm lint deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES)

helm-template:
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) >/dev/null
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set ingress=null >/dev/null
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --show-only templates/configmap.yaml | grep -q '"DetectionRetention":"1100.00:00:00"'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --show-only templates/service.yaml | grep -A2 '^  labels:' | grep -q 'app.kubernetes.io/name: perf-sentinel-hub'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --show-only templates/service.yaml | grep -q -- '- name: http'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set ingress.enabled=true --set ingress.host=hub.example.com --set ingress.tlsSecretName=hub-tls | grep -q 'secretName: "hub-tls"'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set ingress.enabled=true 2>&1 >/dev/null | grep -q 'ingress.host is required'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) --set 'hub.auth.requiredGroups={/hub-users}' --show-only templates/configmap.yaml | grep -q '"RequiredGroups":\["/hub-users"\]'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set 'hub.auth.requiredGroups={/hub-users}' 2>&1 >/dev/null | grep -q '/hub/auth/requiredGroups'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set hub.access=null --show-only templates/configmap.yaml | grep -q '"Access":{}'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set hub.access.adminGroup=x 2>&1 >/dev/null | grep -q 'adminGroup'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) --show-only templates/configmap.yaml | grep -q '"SubjectClaim":"sub"'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) --set hub.auth.subjectClaim=email 2>&1 >/dev/null | grep -q '/hub/auth/subjectClaim'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) --set 'hub.auth.trustedProxyNetworks={10.42.0.0/16}' --show-only templates/configmap.yaml | grep -q '"TrustedProxyNetworks":\["10.42.0.0/16"\]'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) --show-only templates/configmap.yaml | grep -q '"TrustedProxyNetworks":\[\]'
	out=$$(helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set hub.metricsPort=9464) && printf '%s' "$$out" | grep -q 'value: "8080;9464"' && printf '%s' "$$out" | grep -q 'name: metrics, containerPort: 9464' && printf '%s' "$$out" | grep -q '"MetricsPort":9464' && printf '%s' "$$out" | grep -A2 -- '- name: metrics' | grep -q 'port: 9464'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set hub.metricsPort=8080 2>&1 >/dev/null | grep -q '/hub/metricsPort'
	out=$$(helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --show-only templates/deployment.yaml) && printf '%s' "$$out" | grep -q 'startupProbe: { httpGet: { path: /health/ready, port: http }, periodSeconds: 5, failureThreshold: 60 }' && printf '%s' "$$out" | grep -q 'livenessProbe: { httpGet: { path: /health/live, port: http }, periodSeconds: 10, timeoutSeconds: 5, failureThreshold: 6 }'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set probes.startup.failureThreshold=120 --show-only templates/deployment.yaml | grep -q 'periodSeconds: 5, failureThreshold: 120 }'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set probes.startup.failureThreshold=0 2>&1 >/dev/null | grep -q '/probes/startup/failureThreshold'
	out=$$(helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES)) && printf '%s' "$$out" | grep -q 'memory: 1Gi' && printf '%s' "$$out" | grep -q '"MinFreeMemoryMiB":256' && printf '%s' "$$out" | grep -q '"NewSignaturesPerHourWarning":1000'
	out=$$(helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set 'sources[0].pgStat.prometheus=http://vm:8428' --set-string 'sources[0].pgStat.selector=db_role="primary"' --set 'sources[0].pgStat.top=20' --set 'sources[0].pgStat.authHeaderName=Authorization' --set 'sources[0].pgStat.authSecretName=vm' --set 'sources[0].pgStat.authSecretKey=token') && printf '%s' "$$out" | grep -q '"PgStat":{"AuthHeaderName":"Authorization","CallsMetric":null,"Metric":null,"Prometheus":"http://vm:8428","QueryLabel":null,"Selector":"db_role=\\"primary\\"","Top":20,"Unit":null}' && printf '%s' "$$out" | grep -q 'name: Hub__Sources__0__PgStat__AuthHeaderValue' && ! printf '%s' "$$out" | grep -q 'MySqlStat'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set 'sources[0].mysqlStat.metric=m' 2>&1 >/dev/null | grep -q "/sources/0/mysqlStat': missing property 'prometheus'"
	out=$$(helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES)) && ! printf '%s' "$$out" | grep -q 'Disclosures'
	out=$$(helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set hub.disclosures.importSecretName=hub-disclosures --set hub.disclosures.importSecretKey=import --set 'hub.disclosures.webhooks[0].name=auditors' --set 'hub.disclosures.webhooks[0].secretName=hub-webhooks' --set 'hub.disclosures.webhooks[0].urlKey=auditors-url' --set 'hub.disclosures.webhooks[0].secretKey=auditors-secret') && printf '%s' "$$out" | grep -q '"Disclosures":{"AllowInternal":false,"ShareLinkDays":7,"Webhooks":\[{"IncludeShareLink":false,"Name":"auditors","ShareLinkDays":7}\]}' && printf '%s' "$$out" | grep -q 'name: Hub__Disclosures__ImportKey' && printf '%s' "$$out" | grep -q 'name: Hub__Disclosures__Webhooks__0__Url'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set hub.disclosures.expectedIdentity=x 2>&1 >/dev/null | grep -q '/hub/disclosures'
	out=$$(helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES)) && printf '%s' "$$out" | grep -q '"Recaps":{"MaxDelay":"06:00:00","MovementThreshold":0.2,"Retention":"365.00:00:00","Subscriptions":\[\],"Webhooks":\[\]}' && ! printf '%s' "$$out" | grep -q 'Hub__Recaps__'
	out=$$(helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_RECAP_VALUES)) && printf '%s' "$$out" | grep -q '"Smtp":{"From":"hub@example.com","Host":"smtp.example.com","Port":587,"StartTls":true,"UserName":"hub"},"Subscriptions":\[{"cadences":\["week"\],"emails":\["lead@example.com"\],"name":"shop","views":\["pilotage"\],"webhooks":\["team"\]}\],"Webhooks":\[{"Name":"team"}\]' && printf '%s' "$$out" | grep -q 'name: Hub__Recaps__Webhooks__0__Secret' && printf '%s' "$$out" | grep -q 'name: Hub__Recaps__Smtp__Password' && ! printf '%s' "$$out" | grep -q 'smtp-password"'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set hub.cards.organisation=Acme --set hub.cards.domain=acme.example --show-only templates/configmap.yaml | grep -q '"Cards":{"Domain":"acme.example","MaxConcurrentRenders":2,"Organisation":"Acme"}'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set hub.cards.organisation=Acme --set hub.cards.maxConcurrentRenders=4 --show-only templates/configmap.yaml | grep -q '"MaxConcurrentRenders":4'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_RECAP_VALUES) --set hub.recaps.smtp.host= 2>&1 >/dev/null | grep -q '/hub/recaps/smtp/host'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_RECAP_VALUES) --set hub.recaps.smtp.port=465 2>&1 >/dev/null | grep -q '/hub/recaps/smtp/port'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_RECAP_VALUES) --set hub.recaps.smtp.startTls=false 2>&1 >/dev/null | grep -q '/hub/recaps/smtp/host'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_RECAP_VALUES) --set 'hub.recaps.subscriptions[0].webhooks={other}' 2>&1 >/dev/null | grep -q 'recap subscription shop names an undeclared webhook: other'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_RECAP_VALUES) --set 'hub.recaps.subscriptions[1].name=shop' --set 'hub.recaps.subscriptions[1].cadences={day}' --set 'hub.recaps.subscriptions[1].views={tech}' 2>&1 >/dev/null | grep -q 'duplicate recap subscription name: shop'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) $(HELM_ACCESS_VALUES) $(HELM_ACCESS_ON) | grep -q 'name: Hub__Access__MachineTokens__0__Token'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_ACCESS_VALUES) $(HELM_ACCESS_ON) 2>&1 >/dev/null | grep -q '/hub/auth/enabled'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) $(HELM_ACCESS_VALUES) $(HELM_ACCESS_ON) --set hub.ackRelay.trustIdentityHeader=true 2>&1 >/dev/null | grep -q '/hub/ackRelay/trustIdentityHeader'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) $(HELM_ACCESS_VALUES) $(HELM_ACCESS_ON) --set 'hub.access.adminGroups=null' 2>&1 >/dev/null | grep -q '/hub/access'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) $(HELM_ACCESS_VALUES) --set hub.access.enabled=true 2>&1 >/dev/null | grep -q "missing property 'metricsPort'"
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) $(HELM_ACCESS_VALUES) $(HELM_ACCESS_ON) --set 'hub.auth.requiredGroups={/perf-sentinel-hub}' --show-only templates/configmap.yaml | grep -q '"enabled":true'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_ACCESS_VALUES) $(HELM_SECOND_TOKEN) --set 'hub.access.machineTokens[1].name=ci-shop' 2>&1 >/dev/null | grep -q 'duplicate machine token name: ci-shop'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_ACCESS_VALUES) $(HELM_SECOND_TOKEN) --set 'hub.access.machineTokens[1].name=CI' 2>&1 >/dev/null | grep -q 'machine token name must match'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_ACCESS_VALUES) --set 'hub.access.machineTokens[0].secretName=' 2>&1 >/dev/null | grep -q 'machine token ci-shop needs secretName and secretKey'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) --set 'hub.access.machineTokens[0].name=ci-shop' --set 'hub.access.machineTokens[0].group=internal:ci-shop' --set-string 'hub.access.machineTokens[0].expiresAt=2027-06-30T00:00:00Z' --set 'hub.access.machineTokens[0].secretKey=ci-shop' 2>&1 >/dev/null | grep -q "/hub/access/machineTokens/0': missing property 'secretName'"
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_ACCESS_VALUES) --set-string 'hub.access.machineTokens[0].expiresAt=end of june' 2>&1 >/dev/null | grep -q '/hub/access/machineTokens/0/expiresAt'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_ACCESS_VALUES) --show-only templates/configmap.yaml | grep -q '"machineTokens":\[{"expiresAt":"2027-06-30T00:00:00Z","group":"internal:ci-shop","name":"ci-shop"'
	out=$$(helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) $(HELM_ACCESS_VALUES)) && printf '%s' "$$out" | grep -q '"Access":{"adminGroups":\["/hub-admins"\]' && ! printf '%s' "$$out" | grep -q 'Hub__Access__'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) $(HELM_ACCESS_VALUES) $(HELM_ACCESS_ON) $(HELM_LOCAL_ACCOUNT) --set 'hub.auth.trustedProxyNetworks={10.42.0.0/16}' | grep -q 'name: Hub__Access__LocalAccounts__0__PasswordHash'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) $(HELM_ACCESS_VALUES) $(HELM_ACCESS_ON) $(HELM_LOCAL_ACCOUNT) 2>&1 >/dev/null | grep -q '/hub/auth/trustedProxyNetworks'
	out=$$(helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_AUTH_VALUES) $(HELM_ACCESS_VALUES) $(HELM_LOCAL_ACCOUNT)) && printf '%s' "$$out" | grep -q '"localAccounts":\[{"name":"ops","secretKey":"ops","secretName":"hub-break-glass"}\]' && ! printf '%s' "$$out" | grep -q 'Hub__Access__LocalAccounts'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_ACCESS_VALUES) $(HELM_LOCAL_ACCOUNT) --set 'hub.access.localAccounts[1].name=ops' --set 'hub.access.localAccounts[1].secretName=hub-break-glass' --set 'hub.access.localAccounts[1].secretKey=other' 2>&1 >/dev/null | grep -q 'duplicate local account name: ops'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_ACCESS_VALUES) $(HELM_LOCAL_ACCOUNT) --set 'hub.access.localAccounts[0].name=Ops' 2>&1 >/dev/null | grep -q 'local account name must match'
	helm template test deploy/helm/perf-sentinel-hub $(HELM_TEST_VALUES) $(HELM_ACCESS_VALUES) $(HELM_LOCAL_ACCOUNT) --set 'hub.access.localAccounts[0].secretKey=' 2>&1 >/dev/null | grep -q 'local account ops needs secretName and secretKey'

release-check:
	@test -n "$(VERSION)" || { echo "VERSION is required" >&2; exit 2; }
	python3 scripts/check-version.py "v$(VERSION)"

verify-fast: tool-restore format python-tests js-tests coverage-check analysis-config-check badge-check

verify: verify-fast publish audit image-scan helm-lint helm-template

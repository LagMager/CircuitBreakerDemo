# CircuitBreakerDemo

API pequeña en ASP.NET Core para la presentación **«Demo en vivo: sin breaker → con breaker, con números antes vs. después»**. Usa Polly para el circuit breaker e `IHttpClientFactory` para las llamadas HTTP. La dependencia simulada está en la misma aplicación; no hay que iniciar otro servicio.

## Demo en vivo: tabla en el navegador

Desde esta carpeta, inicia la aplicación:

```sh
dotnet run --no-launch-profile --urls http://localhost:5050
```

Abre **http://localhost:5050/compare** en el navegador. La página ejecuta los dos escenarios y muestra una tabla con los resultados medidos. Puedes pulsar «Repetir comparación» para hacer la demo otra vez; cada comparación usa un breaker nuevo.

La página de comparación responde HTTP 200 para poder mostrar la tabla. Las llamadas a la dependencia sí responden HTTP 503, y la tabla cuenta esas respuestas y las llamadas evitadas.

## Alternativa: ejecutar cada modo por separado

Usa una terminal para la aplicación y otra para `curl`.

### 1. Antes: sin breaker

```sh
USE_CIRCUIT_BREAKER=false dotnet run --no-launch-profile --urls http://localhost:5050
```

```sh
curl -s 'http://localhost:5050/demo/run'
```

### 2. Después: con breaker

Detén la aplicación con Ctrl+C y reiníciala:

```sh
USE_CIRCUIT_BREAKER=true dotnet run --no-launch-profile --urls http://localhost:5050
```

```sh
curl -s 'http://localhost:5050/demo/run'
```

`/demo/run` hace diez intentos consecutivos contra una dependencia que demora 300 ms y responde HTTP 503. La página `/compare` hace ambos escenarios en una sola ejecución. Estos son los resultados que obtuve al probarlos por separado:

| Métrica | Sin breaker | Con breaker |
| --- | ---: | ---: |
| Intentos | 10 | 10 |
| Llamadas que llegaron a la dependencia | 10 | 3 |
| Llamadas evitadas | 0 | 7 |
| Tiempo total medido | 3.079 ms | 951 ms |

El tiempo bajó aproximadamente **69 %** en esta prueba y se evitaron **7 de 10** llamadas a la dependencia. Los tiempos exactos pueden variar entre equipos. El breaker no arregla el servicio: evita seguir esperando por un servicio que ya está fallando.

**Guion breve:** «Sin breaker, los diez intentos llegan al servicio y esperamos diez veces. Con breaker, Polly detecta tres fallos, abre el circuito y rechaza de inmediato los siete intentos restantes. Por eso bajan las llamadas al servicio y el tiempo total».

## Mostrar cuándo se abre el circuito

Después de reiniciar con el breaker activado, ejecuta esto dentro de 30 segundos:

```sh
for i in 1 2 3 4; do curl -s 'http://localhost:5050/demo?fail=true'; echo; done
```

Las tres primeras respuestas indican `dependencyWasCalled: true`. En la tercera, `circuitState` cambia a `Open`. La cuarta indica `dependencyWasCalled: false` y `totalDependencyCalls` permanece en 3. Reinicia la aplicación antes de volver a usar `/demo/run` para comenzar con el circuito cerrado.

## Recuperación

Diez segundos después de abrirse, el breaker permite una llamada de prueba. Si la dependencia está sana, el circuito vuelve a `Closed`:

```sh
curl -s 'http://localhost:5050/demo?fail=false'
```

## Código en pocas palabras

- `/dependency?fail=true` espera 300 ms y responde HTTP 503; `fail=false` responde HTTP 200.
- `/demo/run` hace diez llamadas fallidas y devuelve conteos y tiempo total.
- `/compare` ejecuta ambos modos con un breaker nuevo y muestra la tabla en HTML.
- `/demo?fail=true` muestra una llamada individual y si alcanzó la dependencia.
- Polly abre el circuito tras tres respuestas HTTP 503 en un período de 30 segundos; lo mantiene abierto 10 segundos.
- `USE_CIRCUIT_BREAKER` se lee al iniciar. Por defecto es `true`.

El estado del breaker y los contadores están en memoria y se reinician con la aplicación.

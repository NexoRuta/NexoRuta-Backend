# ADR: avisos HTTP durables con snapshot de suscripción

Estado: implementado, 08/11/2026 UTC (07/11 America/Montevideo). Alcance: requisito 28, §6.8/§6.9 y avisos de cambios 36/41.

La bandeja Commerce existente consumía Kafka. No entregaba al sistema externo del comercio, ni podía acreditar retries HTTP, firma o respuesta perdida. Los cambios de negocio ya capturan hechos canónicos en PostgreSQL y una outbox transaccional.

Decisión: derivar una outbox de entregas HTTP mediante un trigger diferido en la transacción de la outbox canónica, copiando suscripción, versión, URL y secreto protegido. Un modo independiente del worker .NET despacha esas filas con leases, fencing y SKIP LOCKED. No requiere disponibilidad del broker para la entrega HTTP. No se cambia el circuito Kafka ni sus contratos.

Alternativas consideradas:

- HTTP desde el endpoint de negocio: acopla admisión/entrega al receptor y no resuelve commit ambiguo ni caída de proceso. Rechazada.
- Usar solo la bandeja Kafka como receptor: no prueba integración HTTP externa. Rechazada.
- Consumir Kafka y consultar la configuración vigente al enviar: introduce otra ventana de fanout y puede redirigir eventos históricos al editar. Puede diseñarse correctamente con snapshots y confirmaciones adicionales, pero agrega coordinación innecesaria para los hechos transaccionales disponibles.
- Encolar una fila por evento sin snapshot: menos almacenamiento, pero no conserva el destino y clave originales. Rechazada.
- Asimetría Ed25519: evita compartir el secreto con el receptor y es apropiada si los consumidores no pueden custodiar claves simétricas. Se eligió el perfil HMAC-SHA256 de Standard Webhooks por su interoperabilidad y soporte nativo actual; no se inventa criptografía.
- SQLite para receptor independiente frente a la base central o memoria: SQLite conserva efecto+deduplicación atómicamente y mantiene la frontera de sistema externo sin infraestructura adicional. La bandeja es de ensayo, explícitamente habilitada y protegida.

Consecuencias: más almacenamiento e índices; la admisión depende de poder persistir la entrega, pero no de disponibilidad HTTP. La captura diferida ve el conjunto final de bultos y conserva estado/versión del evento. No hay promesa de orden global. Se necesitan política explícita de egress, key ring compartido, monitoreo de fallidos y gestión de retención auditada. El envío es al menos una vez con límite; las respuestas perdidas exigen idempotencia del consumidor. Rotación drena los snapshots anteriores; revocación cancela pendientes y no puede retirar un HTTP ya procesado. El historial conserva revisiones y actores; no se borran colas agotadas.

El receptor devuelve 503 para una falla **simulada** y puede interrumpir una respuesta después del commit. El E2E además detiene el proceso receptor para obtener conexión realmente rechazada. Dos workers y reinicios se ensayan con servicios sintéticos y roles restringidos; no se accede a sistemas de terceros ni se despliega.

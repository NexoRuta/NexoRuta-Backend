# CU-XX — Nombre del caso de uso

> Plantilla para copiar al implementar un CU. Este archivo no representa un CU implementado. Eliminar instrucciones y apartados no aplicables antes de abrir la PR.

- **Estado:** en diseño / implementado / pendiente de integración.
- **Módulo responsable:** MXX — nombre.
- **Responsables:** integrantes que implementaron o revisaron.
- **Fecha de última actualización:** AAAA-MM-DD.
- **Fuente:** sección y número del requisito del laboratorio; dependencia de la matriz si existe.
- **Aplicación o interfaz:** portal, backoffice, API, móvil o Worker.

## Objetivo y actores

Describir qué resultado de negocio obtiene el actor y quién puede iniciarlo.

## Disparador, precondiciones y entrada

Indicar cuándo comienza, qué debe existir antes y qué datos recibe. Distinguir los datos enviados por el cliente del contexto de operador/comercio resuelto por el servidor.

## Resultado y flujo principal

Enumerar los pasos observables y los cambios persistidos. Definir la salida que recibe el actor, sin depender de detalles internos del controlador.

## Alternativas, errores y reglas de negocio

Describir datos inválidos, conflictos, ausencia de recursos, fallos externos y sus resultados esperados. Enumerar invariantes, configuración/versiones usadas y transiciones de estado permitidas. No inventar reglas que el equipo aún no haya acordado: dejarlas en `Decisiones pendientes`.

## Seguridad y datos

Indicar perfiles, operador y comercio autorizados; verificaciones de pertenencia; datos personales expuestos; y medidas para impedir lecturas o escrituras de otro tenant. Especificar persistencia, límite transaccional, concurrencia e idempotencia cuando apliquen.

## Dependencias y efectos secundarios

Indicar contratos con otros módulos, eventos, outbox, Worker, notificaciones, caché, invalidación y mensajes de error. Escribir «No aplica» cuando se haya evaluado y no exista esa dependencia.

## Contrato de la interfaz

Registrar ruta y método HTTP, estructura de entrada/salida y códigos de respuesta, o el contrato de mensaje si el CU es asíncrono. Enlazar una especificación OpenAPI o de mensajes si existe.

## Criterios de aceptación y verificación

- Escenario verificable 1.
- Escenario de error o límite relevante.
- Pruebas ejecutadas y resultado.

## Decisiones pendientes y limitaciones

Registrar aquello que todavía necesita acuerdo o que queda fuera del corte actual, con responsable y próximo hito si se conocen.

## Bitácoras individuales

Enlazar las entradas fechadas de todos los integrantes que participaron en este CU.

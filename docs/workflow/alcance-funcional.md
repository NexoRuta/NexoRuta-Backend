# Alcance funcional y monitoreos

Resumen para el equipo de `Laboratorio__NET_2026.pdf` (§§ 4, 5 y 8.3), `Entrega_Analisis_Diseno_2026-10-04.pdf` (pp. 8 y 11–12) y la planilla `Casos de Uso - Responsables.xlsm`. **No reemplaza la especificación de cada CU:** esa se crea en `docs/cu/` al implementarlo, con reglas, contratos y pruebas concretos.

## CU obligatorios

| ID | Módulo | Alcance mínimo |
| --- | --- | --- |
| CU-01 | M01 | Gestionar comercios y usuarios, con perfiles y permisos. |
| CU-02 | M01 | Definir zonas de cobertura y franjas horarias disponibles por zona. |
| CU-03 | M01 | Configurar tarifas por zona, peso, volumen y modalidad, con recargos y bonificaciones, sin despliegue. |
| CU-04 | M01 | Configurar intentos, intervalos, pruebas exigidas, motivos, devoluciones y plazos de servicio. |
| CU-05 | M01 | Configurar marca, colores y contacto del operador para portales y comunicaciones. |
| CU-06 | M03 | Gestionar repartidores y vehículos, incluida su capacidad de carga. |
| CU-07 | M02 | Dar de alta envíos individualmente, por archivo y por API; la importación masiva debe ser idempotente. |
| CU-08 | M02 | Registrar uno o más bultos por envío, con código, peso y dimensiones. |
| CU-09 | M02 | Calcular al alta el precio según la versión tarifaria vigente. |
| CU-10 | M02 | Recibir bultos por escaneo y registrar discrepancias respecto de lo declarado. |
| CU-11 | M02 | Aplicar una máquina de estados explícita, con transiciones válidas verificables. |
| CU-12 | M02 | Guardar eventos de envío inmutables con tiempo, origen, responsable y ubicación cuando corresponda. |
| CU-13 | M03 | Armar rutas asignando envíos a un repartidor y un vehículo. |
| CU-14 | M03 | Validar límite de paradas, peso, volumen y compatibilidad con franjas comprometidas. |
| CU-15 | M03 | Impedir la asignación simultánea del mismo envío a dos rutas. |
| CU-16 | M03 | Ordenar paradas con criterio razonable y documentado; la optimización no es obligatoria. |
| CU-17 | M03 | Despachar la ruta y hacerla disponible para la aplicación del repartidor. |
| CU-18 | M04 | Descargar la hoja de ruta del día para uso sin conexión. |
| CU-19 | M04 | Escanear la carga y alertar bultos faltantes o sobrantes. |
| CU-20 | M04 | Registrar entrega con la prueba exigida, hora y posición del dispositivo. |
| CU-21 | M04 | Registrar intento fallido con motivo configurado y evidencia. |
| CU-22 | M04 | Gestionar devoluciones y rendición al volver al depósito. |
| CU-23 | M04 | Seguir la posición del vehículo durante la jornada. |
| CU-24 | M04 | Operar sin red y sincronizar después con política explícita de conflictos. |
| CU-25 | M05 | Ofrecer seguimiento público por enlace sin revelar IDs internos ni datos de terceros. |
| CU-26 | M05 | Permitir solicitud de reprogramación sujeta a las reglas del operador. |
| CU-27 | M05 | Notificar al destinatario ante cambios de estado relevantes. |
| CU-28 | M05 | Enviar avisos HTTP firmados a sistemas de comercios, con suscripción, reintentos y fallidos. |
| CU-29 | M06 | Mostrar flota y envíos en riesgo de incumplir la ventana en un tablero en vivo. |
| CU-30 | M06 | Reportar cumplimiento, motivos de no entrega, volumen y liquidación por comercio. |

## Capacidades explícitas de las aplicaciones sin CU numerado

La sección 4 de la letra incluye estas capacidades dentro del alcance obligatorio. Requieren definición funcional cuando se implementen.

| ID | Módulo | Capacidad |
| --- | --- | --- |
| AP-01 | M06 | Registrar y resolver incidencias en el backoffice. |
| AP-02 | M02 | Imprimir etiquetas desde el portal del comercio. |
| AP-03 | M02 | Consultar y seguir los envíos propios del comercio. |
| AP-04 | M02 | Consultar y gestionar devoluciones del comercio. |
| AP-05 | M05 | Configurar la suscripción del comercio a avisos automáticos. |
| AP-06 | M06 | Consultar cuenta corriente y liquidaciones del comercio. |
| AP-07 | M05 | Mostrar la ventana horaria estimada en seguimiento público. |
| AP-08 | M04 | Navegar entre las paradas de la ruta en la aplicación móvil. |
| AP-09 | M06 | Ejecutar el cierre diario mediante el Worker. |

## Opcionales seleccionados en el diseño

La entrega selecciona seis opciones que suman **14 puntos**, frente al mínimo de 12 para un equipo de cuatro. Su selección no implica que estén implementadas; los puntos se obtienen solo al cumplir cada requisito completo.

| ID | Puntos | Compromiso seleccionado |
| --- | ---: | --- |
| OPT-03 | 3 | Kafka para eventos de flota, con más de un consumidor y reconstrucción por replay; debe coexistir con la cola de trabajo obligatoria. |
| OPT-05 | 2 | NoSQL para historial y analítica, con índices, retención y API de lectura paginada y filtrable. |
| OPT-06 | 2 | Pruebas de carga automatizadas en el ambiente remoto, incluida la sincronización simultánea. |
| OPT-07 | 2 | Cobertura superior al 70 % en dominio y aplicación, reportada por el pipeline. |
| OPT-08 | 2 | Pruebas de extremo a extremo de flujos críticos integradas al pipeline. |
| OPT-09 | 3 | Notificaciones push reales con la app cerrada ante reasignación urgente. |

## Hitos de monitoreo de 2026

| Fecha | Demostración requerida |
| --- | --- |
| 08/10 | `docker compose up` levanta el entorno; un envío creado desde el portal aparece en el backoffice; corre el pipeline de CI. La planilla de recorte exige operador, comercio y usuario de prueba vinculados, alta individual y al menos un bulto. No exige cerrar aún ABM completo, precio, estados ni autorización por perfil. |
| 15/10 | Máquina de estados operativa, dos operadores con configuraciones aisladas y autenticación/autorización por perfil. |
| 22/10 | App móvil descarga ruta, registra entregas sin conexión y sincroniza; caché con métrica de aciertos. |
| 29/10 | Cola y Worker independientes, avisos a comercios con reintentos y fallidos, tablero en tiempo real; se anuncia un cambio de requerimientos obligatorio. |
| 05/11 | Nube e IaC, dos instancias API balanceadas y tablero de observabilidad. |
| 12/11 | Opcionales completos y ensayo de la demostración. |
| 15/11 | Entrega final. |

La [arquitectura del backend](arquitectura-backend.md) resume requisitos no funcionales y decisiones. Cada implementación debe dejar su especificación concreta según la [guía de CU](implementar-cu.md).

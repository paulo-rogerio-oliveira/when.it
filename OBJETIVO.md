# Objetivo deste repositório

> Repositório `when.it` — **DbSense**.

## Por que este repositório existe

Sistemas corporativos em produção guardam a informação que importa no banco de
dados, mas não a publicam: não emitem eventos, não têm webhook, não têm fila. Quando
o negócio precisa reagir a um fato ("lançamento contábil aprovado", "nota fiscal
cancelada"), as saídas de sempre são ruins — alterar a aplicação (nem sempre é
possível, às vezes nem há mais quem a mantenha), espalhar triggers no banco (invisíveis,
frágeis e dentro da transação do usuário) ou fazer polling (atrasado, caro e cego
para o que aconteceu entre duas leituras).

Este repositório existe para resolver isso de fora: **transformar operações DML de
uma aplicação alvo em eventos de domínio e reações automáticas, sem tocar em uma
linha do código dela nem interferir nas suas transações.**

## A proposta de valor

O ciclo completo, observando o SQL Server pelo lado de fora:

1. **Captura** — Extended Events (`sql_batch_completed` / `rpc_completed`) entregam o SQL
   que a aplicação executou, sem trigger e fora da transação do usuário.
2. **Gravação assistida** — o desenvolvedor abre uma gravação pela UI, executa a operação
   real na aplicação alvo e o DbSense persiste os eventos já com o SQL parseado.
3. **Inferência da regra** — a partir do que foi gravado, o sistema propõe uma *regra*
   declarativa (trigger + companions), por heurística (ScriptDom) ou por LLM (opt-in).
4. **Matching** — em produção, a engine stateful correlaciona os eventos por janela
   temporal ou por transação até a regra fechar, com idempotência por trigger.
5. **Reação** — o evento fechado vai para um outbox transacional e é executado com retry:
   processo (`cmd`), SQL (`sql`) ou mensagem RabbitMQ (`rabbit`).

O diferencial não é ler o log do banco: é **fechar o ciclo sem invadir a aplicação
alvo** — descobrir a regra a partir de uma operação real, correlacionar o que é um
único fato de negócio espalhado em vários comandos e entregar a reação com garantia
de entrega.

## O que este repositório entrega

| Parte | Papel |
|---|---|
| `src/DbSense.Api/` | API REST: auth, setup wizard, conexões, recordings, rules, dashboard |
| `src/DbSense.Worker/` | Hosted services: coleta XE, matcher, executor de reactions, comandos |
| `src/DbSense.Core/` | Domínio, EF, parser SQL, inferência, engine de matching, reactions, segurança |
| `src/DbSense.Contracts/` | DTOs compartilhados entre API, Worker e frontend |
| `frontend/` | UI em Vite + React + TS: gravar, revisar, inferir e publicar regras |
| `electron/` | Shell desktop que sobe API + Worker + UI numa janela só, empacotável em `.exe` |
| `sandbox/` | App de contabilidade (.NET + React) usado como alvo real para validar o pipeline |
| `docs/` | Arquitetura interna, guia de reactions e checklist de produção |

## O que este repositório NÃO se propõe a ser

- **Não é um CDC/replicação.** O objetivo não é espelhar linhas em outro banco, e sim
  reconhecer *fatos de negócio* e disparar reações a partir deles.
- **Não é um ESB nem um orquestrador de workflow.** Ele emite o evento e executa uma
  reação; coreografia, compensação e roteamento complexo ficam com quem consome.
- **Não altera a aplicação alvo.** É observação de fora, sem trigger, sem schema novo
  no banco do alvo e sem entrar na transação do usuário — a aplicação nunca fica mais
  lenta nem quebra por causa do DbSense.
- **Não enxerga tudo o que o banco faz.** O parser lê o SQL capturado, não o corpo de
  stored procedures; operações encapsuladas em procs ficam fora do alcance no MVP
  (ver "Limitações conhecidas" no [README.md](README.md)).
- **Não substitui o modelo de eventos de quem pode mudar o código.** Se a aplicação
  alvo pode publicar seus próprios eventos, ela deveria — o DbSense é para quando ela
  não pode.

## Critério de sucesso

Uma operação executada na aplicação alvo, sem nenhuma alteração nela, vira um evento
de domínio identificado corretamente e uma reação executada uma única vez — descoberta
por gravação em minutos, e não por engenharia reversa do banco.

## Por onde continuar

- [README.md](README.md) — estado atual, instalação, setup inicial e fluxo principal
- [docs/architecture.md](docs/architecture.md) — pipeline interno (XE, parser, engine, idempotência)
- [docs/reactions.md](docs/reactions.md) — tipos de reaction, placeholders e troubleshooting
- [docs/production.md](docs/production.md) — permissões, retenção, segurança e operação
- [sandbox/README.md](sandbox/README.md) — app alvo para exercitar o pipeline
- `spec-dbsense-mvp.md` — spec original do MVP

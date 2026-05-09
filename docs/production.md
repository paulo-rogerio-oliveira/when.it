# Produção

Checklist para rodar o DbSense perto de bancos reais. Este documento não substitui
validação de infraestrutura, mas deixa explícitos os pontos que precisam estar decididos
antes de ativar regras em produção.

## Permissões SQL Server

O usuário usado pelo DbSense precisa conseguir:

- conectar no banco alvo;
- criar, iniciar, parar e remover Extended Events sessions;
- ler o ring buffer da session;
- ler metadados suficientes para validar database/schema/tabela;
- executar as reactions SQL configuradas, quando esse tipo de reaction for usado.

Evite usar `sa` ou usuário dono do banco em produção. Prefira um login dedicado por ambiente
e limite as permissões de escrita apenas aos objetos que podem ser alterados por reactions SQL.

## Impacto no banco alvo

As sessions de XEvents capturam `sql_batch_completed` e `rpc_completed` para observar DMLs.
Antes de deixar uma conexão ativa por muito tempo:

- valide filtros de database, host, aplicação e login quando estiver gravando;
- mantenha poucas gravações simultâneas;
- acompanhe CPU, memória e I/O do SQL Server durante os primeiros testes;
- evite capturar tráfego massivo sem uma regra de negócio clara;
- documente quais aplicações e bancos estão sendo observados.

O Worker marca suas próprias conexões com `ApplicationName = "DbSense.Worker"` para evitar
captura recursiva de tráfego gerado pelo próprio DbSense.

## Retenção de dados

Defina uma política para as tabelas operacionais:

- `dbsense.recording_events`: pode crescer rapidamente durante gravações. Remova gravações
  antigas quando a regra derivada não precisar mais ser revisada.
- `dbsense.events_log`: é o histórico de matches e a base de idempotência. Retenha pelo
  período necessário para auditoria e reprocessamento.
- `dbsense.outbox`: mantenha `pending` e `failed` visíveis para operação. Registros `sent`
  podem ser arquivados após a janela de auditoria.
- `dbsense.worker_commands`: pode ser limpo depois que comandos antigos estiverem em status
  final.

Não apague `events_log` sem entender a consequência na idempotência: uma mesma captura
reentregue pode voltar a publicar se a chave histórica tiver sido removida.

## Segurança e secrets

Configure secrets fora do repositório e fora de imagens Docker:

- `ConnectionStrings__ControlDb`
- `Security__EncryptionKey`
- `Security__JwtSecret`
- `Llm__ApiKey`, se LLM estiver habilitado
- senhas das conexões SQL e destinos RabbitMQ

Use chaves diferentes por ambiente. Rotacione credenciais quando alguém com acesso ao ambiente
sair do time ou quando um secret for exposto.

## Reactions

Antes de ativar uma rule:

- teste a reaction manualmente pela UI;
- confirme que o consumidor é idempotente em relação à chave enviada pelo DbSense;
- defina timeout e número máximo de tentativas compatíveis com o sistema de destino;
- para `sql`, use parâmetros em vez de interpolar valores no texto SQL;
- para `rabbit`, valide exchange, routing key e binding com `mandatory=true`;
- para `cmd`, use executáveis absolutos, timeout curto e allowlist operacional.

`cmd` reaction deve ser tratada como capacidade administrativa. Se o ambiente não precisa
executar processos locais, desabilite essa opção em implantação.

## Backup e restauração

O control DB contém regras, conexões, destinos, usuários, outbox e histórico operacional.
Inclua o banco `dbsense_control` no plano de backup do ambiente.

Teste restauração em outro servidor antes de depender do backup em produção. Depois de restaurar,
valide:

- login administrativo;
- descriptografia de senhas com a mesma `Security__EncryptionKey`;
- status das conexões SQL;
- status dos destinos RabbitMQ;
- workers sem outbox travada por uma instância antiga.

## Operação diária

Monitore pelo menos:

- health da API;
- processo do Worker;
- quantidade de mensagens `pending` e `failed` na outbox;
- regras ativas por conexão;
- gravações ainda em status `recording`;
- erros recentes em conexões SQL e destinos RabbitMQ;
- crescimento das tabelas de eventos.

Ao remover uma gravação, o DbSense também remove as regras/reactions criadas a partir dela,
incluindo event logs e outbox relacionados a essas regras.

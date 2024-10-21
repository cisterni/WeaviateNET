using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using WeaviateNET.Modules;

namespace WeaviateNET
{
    public partial class Schema
    {
        [JsonIgnore]
        internal WeaviateDB? _connection;

        public async Task Update()
        {
            if (_connection == null) throw new Exception("Empty connection while loading schema");
            var s = await _connection.Client.Schema_dumpAsync();
            if (s == null) throw new Exception("Error while loading the Schema");
            this.Classes = s.Classes;
            this.Maintainer = s.Maintainer;
            this.Name = s.Name;

            foreach (var c in this.Classes)
            {
                c._connection = this._connection;
            }
        }

        public static ICollection<FieldInfo> PersistentFields<P>()
        {
            var flds = typeof(P).GetFields();
            return flds.Where(f => !f.CustomAttributes.Where(a => a.AttributeType == typeof(JsonIgnoreAttribute)).Any()).ToList();
        }

        public static ICollection<string> PersistentFieldNames<P>()
        {
            return PersistentFields<P>().Select(f => f.Name).ToList();
        }

        public async Task<WeaviateClass<P>> NewClass<P>(string name) where P : class, new()
        {
            if (_connection == null) throw new Exception($"Empty connection while creating class '{name}'");

            var c = new WeaviateClass<P>() { Name=name };

            var pclass = typeof(P);

            var vectorizer = pclass.GetCustomAttribute<VectorizerAttribute>();
            var vectorIndexConfig = pclass.GetCustomAttribute<VectorIndexConfigAttribute>();
            if (vectorIndexConfig != null) c.VectorIndexConfig = new Dictionary<string, object>(){ { "distance", vectorIndexConfig.Distance } };
            var replicationConfig = pclass.GetCustomAttribute<ReplicationConfigAttribute>();
            if (replicationConfig != null) c.ReplicationConfig.Factor = replicationConfig.Factor;
            var indexStopwords = pclass.GetCustomAttribute<IndexStopwordsAttribute>();
            var indexTimestamps = pclass.GetCustomAttribute<IndexTimestampsAttribute>();
            var indexNullState = pclass.GetCustomAttribute<IndexNullStateAttribute>();
            var indexPropertyLength = pclass.GetCustomAttribute<IndexPropertyLengthAttribute>();
            var bm25indexConfig = pclass.GetCustomAttribute<BM25IndexAttribute>();
            if (vectorizer != null)
            {
                ConfigureClassModulueOptions(c, vectorizer);
            }
            if (c.InvertedIndexConfig == null && 
                (indexStopwords != null || 
                indexTimestamps != null || 
                indexNullState != null || 
                indexPropertyLength != null || 
                bm25indexConfig != null))
                c.InvertedIndexConfig = new InvertedIndexConfig();
            if (indexStopwords != null)
            {
                if (c.InvertedIndexConfig!.Stopwords == null)
                    c.InvertedIndexConfig.Stopwords = new StopwordConfig();
                c.InvertedIndexConfig.Stopwords.Preset = indexStopwords.Preset;
                c.InvertedIndexConfig.Stopwords.Additions = indexStopwords.Additions;
                c.InvertedIndexConfig.Stopwords.Removals = indexStopwords.Removals;
            }
            if (indexTimestamps != null) c.InvertedIndexConfig!.IndexTimestamps = indexTimestamps.Enabled;
            if (indexNullState != null) c.InvertedIndexConfig!.IndexNullState = indexNullState.Enabled;
            if (indexPropertyLength != null) c.InvertedIndexConfig!.IndexPropertyLength = indexPropertyLength.Enabled;
            if (bm25indexConfig != null)
            {
                if (c.InvertedIndexConfig!.Bm25 == null)
                    c.InvertedIndexConfig.Bm25 = new BM25Config();
                c.InvertedIndexConfig.Bm25.K1 = bm25indexConfig.K1;
                c.InvertedIndexConfig.Bm25.B = bm25indexConfig.B;
            }
            var multiTenancyConfig = pclass.GetCustomAttribute<MultiTenancyAttribute>();
            if (multiTenancyConfig != null) 
                c.MultiTenancyConfig = new MultiTenancyConfig() { Enabled = multiTenancyConfig.Enabled };

            var flds = PersistentFields<P>();
            c.Properties = new List<Property>(flds.Count);

            foreach (var f in flds)
            {
                Property? p = null;
                if (f.FieldType == typeof(WeaviateRef) || f.FieldType == typeof(WeaviateRef[]))
                {
                    var a = f.GetCustomAttribute<BeaconToAttribute>();
                    if (a == null)
                        throw new Exception("Custom attribute 'BeaconTo' is required for 'WeaviateRef' fields");
                    if (a.ClassNames == null || a.ClassNames.Length == 0)
                        throw new Exception("Custom attribute 'BeaconTo' must have at least one class name specified");
                    p = Property.CreateRef(f.Name, a.ClassNames);
                }
                else
                {
                    p = Property.Create(f.FieldType, f.Name);
                }
                    
                var tokenizationModeConfig = f.GetCustomAttribute<TokenizationAttribute>();
                if (tokenizationModeConfig != null) p.Tokenization = tokenizationModeConfig.Mode;
                var indexFilterableConfig = f.GetCustomAttribute<IndexFilterableAttribute>();
                if (indexFilterableConfig != null) p.IndexFilterable = indexFilterableConfig.Enabled;
                var indexSearchableConfig = f.GetCustomAttribute<IndexSearchableAttribute>();
                if (indexSearchableConfig != null) p.IndexSearchable = indexSearchableConfig.Enabled;
                var vectorizerp = f.GetCustomAttribute<VectorizerAttribute>();
                if (vectorizerp !=  null) ConfigurePropertyModuleOptions(p, vectorizerp);
                // FIXME: not supporting references yet
                c.Properties.Add(p);
            }
            var nc = await _connection.Client.Schema_objects_createAsync(c);
            

            if (nc == null) throw new Exception($"Error while creating the class '{c.Name}'");
            var ret = new WeaviateClass<P>(nc);
            ret._connection = _connection;
            return ret;
        }

        private static void ConfigureClassModulueOptions<P>(WeaviateClass<P> c, VectorizerAttribute vectorizer) where P : class, new()
        {
            var vtype = vectorizer.GetType();
            switch (vtype)
            {
                case Type when vtype == typeof(AWSVectorizerClassAttribute):
                    var awsv = vectorizer as AWSVectorizerClassAttribute;
                    if (awsv == null) throw new Exception("Vectorizer is not of type 'AWSVectorizerAttribute'");
                    c.Vectorizer = AWSVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Text2Vec.AWSClassModCfg()
                    {
                        Parameters = new Modules.Text2Vec.AWSClassModParams()
                        {
                            model = awsv.model,
                            region = awsv.region,
                            vectorizeClassName = awsv.vectorizeClassName
                        }
                    };
                    break;

                case Type when vtype == typeof(CohereVectorizerClassAttribute):
                    var coherev = vectorizer as CohereVectorizerClassAttribute;
                    if (coherev == null) throw new Exception("Vectorizer is not of type 'CohereVectorizerAttribute'");
                    c.Vectorizer = CohereVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Text2Vec.CohereClassModCfg()
                    {
                        Parameters = new Modules.Text2Vec.CohereClassModParams()
                        {
                            model = coherev.model,
                            truncate = coherev.truncate,
                            baseUrl = coherev.baseUrl,
                            vectorizeClassName = coherev.vectorizeClassName
                        }
                    };
                    break;

                case Type when vtype == typeof(PalmVectorizerClassAttribute):
                    var palmv = vectorizer as PalmVectorizerClassAttribute;
                    if (palmv == null) throw new Exception("Vectorizer is not of type 'PalmVectorizerAttribute'");
                    c.Vectorizer = PalmVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Text2Vec.PalmClassModCfg()
                    {
                        Parameters = new Modules.Text2Vec.PalmClassModParams()
                        {
                            projectId = palmv.projectId,
                            apiEndpoint = palmv.apiEndpoint,
                            modelId = palmv.modelId,
                            titleProperty = palmv.titleProperty,
                            vectorizeClassName = palmv.vectorizeClassName
                        }
                    };
                    break;

                case Type when vtype == typeof(HuggingFaceVectorizerClassAttribute):
                    var hfv = vectorizer as HuggingFaceVectorizerClassAttribute;
                    if (hfv == null) throw new Exception("Vectorizer is not of type 'HuggingFaceVectorizerAttribute'");
                    c.Vectorizer = HuggingFaceVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Text2Vec.HuggingFaceClassModCfg()
                    {
                        Parameters = new Modules.Text2Vec.HuggingFaceClassModParams()
                        {
                            model = hfv.model,
                            endpointURL = hfv.endpointURL,
                            options = hfv.options,
                            passageModel = hfv.passageModel,
                            queryModel = hfv.queryModel,
                            vectorizeClassName = hfv.vectorizeClassName
                        }
                    };
                    break;

                case Type when vtype == typeof(JinaAIVectorizerClassAttribute):
                    var jinav = vectorizer as JinaAIVectorizerClassAttribute;
                    if (jinav == null) throw new Exception("Vectorizer is not of type 'JinaAIVectorizerAttribute'");
                    c.Vectorizer = JinaAIVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Text2Vec.JinaAIClassModCfg()
                    {
                        Parameters = new Modules.Text2Vec.JinaAIClassModParams()
                        {
                            vectorizeClassName = jinav.vectorizeClassName,
                            model = jinav.model
                        }
                    };
                    break;

                case Type when vtype == typeof(OpenAIVectorizerClassAttribute):
                    var v = vectorizer as OpenAIVectorizerClassAttribute;
                    if (v == null) throw new Exception("Vectorizer is not of type 'OpenAIVectorizerAttribute'");
                    c.Vectorizer = OpenAIVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Text2Vec.OpenAIClassModCfg()
                    {
                        Parameters = new Modules.Text2Vec.OpenAIClassModParams()
                        {
                            vectorizeClassName = v.vectorizeClassName,
                            model = v.model,
                            dimensions = v.dimensions,
                            modelVersion = v.modelVersion,
                            type = v.type,
                            baseUrl = v.baseUrl
                        }
                    };
                    break;

                case Type when vtype == typeof(ContextionaryVectorizerClassAttribute):
                    var cv = vectorizer as ContextionaryVectorizerClassAttribute;
                    if (cv == null) throw new Exception("Vectorizer is not of type 'ContextionaryVectorizerAttribute'");
                    c.Vectorizer = ContextionaryVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Text2Vec.ContextionaryClassModCfg()
                    {
                        Parameters = new Modules.Text2Vec.ContextionaryClassModParams()
                        {
                            vectorizeClassName = cv.vectorizeClassName,
                        }
                    };
                    break;

                case Type when vtype == typeof(TransformersVectorizerClassAttribute):
                    var tv = vectorizer as TransformersVectorizerClassAttribute;
                    if (tv == null) throw new Exception("Vectorizer is not of type 'TransformersVectorizerAttribute'");
                    c.Vectorizer = TransformersVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Text2Vec.TransformersClassModCfg()
                    {
                        Parameters = new Modules.Text2Vec.TransformersClassModParams()
                        {
                            vectorizeClassName = tv.vectorizeClassName,
                            poolingStrategy = tv.poolingStrategy
                        }
                    };
                    break;

                case Type when vtype == typeof(GPT4AllVectorizerClassAttribute):
                    var gpt4v = vectorizer as GPT4AllVectorizerClassAttribute;
                    if (gpt4v == null) throw new Exception("Vectorizer is not of type 'GPT4AllVectorizerAttribute'");
                    c.Vectorizer = GPT4AllVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Text2Vec.GPT4AllClassModCfg()
                    {
                        Parameters = new Modules.Text2Vec.GPT4AllClassModParams()
                        {
                            vectorizeClassName = gpt4v.vectorizeClassName
                        }
                    };
                    break;

                case Type when vtype == typeof(NeuralVectorizerClassAttribute):
                    var nv = vectorizer as NeuralVectorizerClassAttribute;
                    if (nv == null) throw new Exception("Vectorizer is not of type 'NeuralVectorizerAttribute'");
                    c.Vectorizer = NeuralVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Img2Vec.NeuralClassModCfg()
                    {
                        Parameters = new Modules.Img2Vec.NeuralClassModParams()
                        {
                            imageFields = nv.imageFields
                        }
                    };
                    break;

                case Type when vtype == typeof(ClipVectorizerClassAttribute):
                    var clv = vectorizer as ClipVectorizerClassAttribute;
                    if (clv == null) throw new Exception("Vectorizer is not of type 'ClipVectorizerAttribute'");
                    c.Vectorizer = ClipVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Multi2Vec.ClipClassModCfg()
                    {
                        Parameters = new Modules.Multi2Vec.ClipClassModParams()
                        {
                            vectorizeClassName = clv.vectorizeClassName,
                            imageFields = clv.imageFields,
                            textFields = clv.textFields,
                            weights = clv.weights

                        }
                    };
                    break;

                case Type when vtype == typeof(BindVectorizerClassAttribute):
                    var bv = vectorizer as BindVectorizerClassAttribute;
                    if (bv == null) throw new Exception("Vectorizer is not of type 'BindVectorizerAttribute'");
                    c.Vectorizer = BindVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Multi2Vec.BindClassModCfg()
                    {
                        Parameters = new Modules.Multi2Vec.BindClassModParams()
                        {
                            vectorizeClassName = bv.vectorizeClassName,
                            imageFields = bv.imageFields,
                            textFields = bv.textFields,
                            audioFields = bv.audioFields,
                            videoFields = bv.videoFields,
                            thermalFields = bv.thermalFields,
                            depthFields = bv.depthFields,
                            IMUFields = bv.IMUFields,
                            weights = bv.weights
                        }
                    };
                    break;

                case Type when vtype == typeof(CentroidVectorizerClassAttribute):
                    var cev = vectorizer as CentroidVectorizerClassAttribute;
                    if (cev == null) throw new Exception("Vectorizer is not of type 'CentroidVectorizerAttribute'");
                    c.Vectorizer = CentroidVectorizerClassAttribute.ModuleName;
                    break;

                case Type when vtype == typeof(RerankerCohereVectorizerClassAttribute):
                    var rcv = vectorizer as RerankerCohereVectorizerClassAttribute;
                    if (rcv == null) throw new Exception("Vectorizer is not of type 'RerankerCohereVectorizerAttribute'");
                    c.Vectorizer = RerankerCohereVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Reranker.RerankerCohereClassModCfg()
                    {
                        Parameters = new Modules.Reranker.RerankerCohereClassModParams()
                        {
                            model = rcv.model
                        }
                    };
                    break;

                case Type when vtype == typeof(RerankerTransformersVectorizerClassAttribute):
                    var rtv = vectorizer as RerankerTransformersVectorizerClassAttribute;
                    if (rtv == null) throw new Exception("Vectorizer is not of type 'RerankerTransformersVectorizerAttribute'");
                    c.Vectorizer = RerankerTransformersVectorizerClassAttribute.ModuleName;
                    c.ModuleConfig = new Modules.Reranker.RerankerTransformersClassModCfg()
                    {
                        Parameters = new Modules.Reranker.RerankerTransformersClassModParams()
                        {
                        }
                    };
                    break;
            }
        }

        private static void ConfigurePropertyModuleOptions(Property p, VectorizerAttribute vectorizerp)
        {
            var vtype = vectorizerp.GetType();
            switch (vtype)
            {
                case Type when vtype == typeof(AWSVectorizerPropertyAttribute):
                    var awsv = vectorizerp as AWSVectorizerPropertyAttribute;
                    if (awsv == null) throw new Exception("Vectorizer is not of type 'AWSVectorizerPropertyAttribute'");
                    p.ModuleConfig = new Modules.Text2Vec.AWSPropertyModCfg()
                    {
                        Parameters = new Modules.Text2Vec.AWSPropertyModParams()
                        {
                            vectorizePropertyName = awsv.vectorizePropertyName,
                            skip = awsv.skip
                        }
                    };
                    break;

                case Type when vtype == typeof(CohereVectorizerPropertyAttribute):
                    var coherev = vectorizerp as CohereVectorizerPropertyAttribute;
                    if (coherev == null) throw new Exception("Vectorizer is not of type 'CohereVectorizerPropertyAttribute'");
                    p.ModuleConfig = new Modules.Text2Vec.CoherePropertyModCfg()
                    {
                        Parameters = new Modules.Text2Vec.CoherePropertyModParams()
                        {
                            vectorizePropertyName = coherev.vectorizePropertyName,
                            skip = coherev.skip
                        }
                    };
                    break;

                case Type when vtype == typeof(PalmVectorizerPropertyAttribute):
                    var palmv = vectorizerp as PalmVectorizerPropertyAttribute;
                    if (palmv == null) throw new Exception("Vectorizer is not of type 'PalmVectorizerPropertyAttribute'");
                    p.ModuleConfig = new Modules.Text2Vec.PalmPropertyModCfg()
                    {
                        Parameters = new Modules.Text2Vec.PalmPropertyModParams()
                        {
                            vectorizePropertyName = palmv.vectorizePropertyName,
                            skip = palmv.skip
                        }
                    };
                    break;

                case Type when vtype == typeof(HuggingFaceVectorizerPropertyAttribute):
                    var hfv = vectorizerp as HuggingFaceVectorizerPropertyAttribute;
                    if (hfv == null) throw new Exception("Vectorizer is not of type 'HuggingFaceVectorizerPropertyAttribute'");
                    p.ModuleConfig = new Modules.Text2Vec.HuggingFacePropertyModCfg()
                    {
                        Parameters = new Modules.Text2Vec.HuggingFacePropertyModParams()
                        {
                            vectorizePropertyName = hfv.vectorizePropertyName,
                            skip = hfv.skip
                        }
                    };
                    break;

                case Type when vtype == typeof(JinaAIVectorizerPropertyAttribute):
                    var jinav = vectorizerp as JinaAIVectorizerPropertyAttribute;
                    if (jinav == null) throw new Exception("Vectorizer is not of type 'JinaAIVectorizerPropertyAttribute'");
                    p.ModuleConfig = new Modules.Text2Vec.JinaAIPropertyModCfg()
                    {
                        Parameters = new Modules.Text2Vec.JinaAIPropertyModParams()
                        {
                            skip = jinav.skip,
                            vectorizePropertyName = jinav.vectorizePropertyName
                        }
                    };
                    break;

                case Type when vtype == typeof(OpenAIVectorizerPropertyAttribute):
                    var v = vectorizerp as OpenAIVectorizerPropertyAttribute;
                    if (v == null) throw new Exception("Vectorizer is not of type 'OpenAIVectorizerPropertyAttribute'");
                    p.ModuleConfig = new Modules.Text2Vec.OpenAIPropertyModCfg()
                    {
                        Parameters = new Modules.Text2Vec.OpenAIPropertyModParams()
                        {
                            vectorizePropertyName = v.vectorizePropertyName,
                            skip = v.skip
                        }
                    };
                    break;

                case Type when vtype == typeof(ContextionaryVectorizerPropertyAttribute):
                    var cv = vectorizerp as ContextionaryVectorizerPropertyAttribute;
                    if (cv == null) throw new Exception("Vectorizer is not of type 'ContextionaryVectorizerPropertyAttribute'");
                    p.ModuleConfig = new Modules.Text2Vec.ContextionaryPropertyModCfg()
                    {
                        Parameters = new Modules.Text2Vec.ContextionaryPropertyModParams()
                        {
                            skip = cv.skip,
                            vectorizePropertyName = cv.vectorizePropertyName
                        }
                    };
                    break;

                case Type when vtype == typeof(TransformersVectorizerPropertyAttribute):
                    var tv = vectorizerp as TransformersVectorizerPropertyAttribute;
                    if (tv == null) throw new Exception("Vectorizer is not of type 'TransformersVectorizerPropertyAttribute'");
                    p.ModuleConfig = new Modules.Text2Vec.TransformersPropertyModCfg()
                    {
                        Parameters = new Modules.Text2Vec.TransformersPropertyModParams()
                        {
                            vectorizePropertyName = tv.vectorizePropertyName,
                            skip = tv.skip
                        }
                    };
                    break;

                case Type when vtype == typeof(GPT4AllVectorizerPropertyAttribute):
                    var gpt4v = vectorizerp as GPT4AllVectorizerPropertyAttribute;
                    if (gpt4v == null) throw new Exception("Vectorizer is not of type 'GPT4AllVectorizerPropertyAttribute'");
                    p.ModuleConfig = new Modules.Text2Vec.GPT4AllPropertyModCfg()
                    {
                        Parameters = new Modules.Text2Vec.GPT4AllPropertyModParams()
                        {
                            skip = gpt4v.skip,
                            vectorizePropertyName = gpt4v.vectorizePropertyName
                        }
                    };
                    break;

                case Type when vtype == typeof(ClipVectorizerPropertyAttribute):
                    var clv = vectorizerp as ClipVectorizerPropertyAttribute;
                    if (clv == null) throw new Exception("Vectorizer is not of type 'ClipVectorizerPropertyAttribute'");
                    p.ModuleConfig = new Modules.Multi2Vec.ClipPropertyModCfg()
                    {
                        Parameters = new Modules.Multi2Vec.ClipPropertyModParams()
                        {
                            skip = clv.skip,
                            vectorizePropertyName = clv.vectorizePropertyName
                        }
                    };
                    break;

                case Type when vtype == typeof(BindVectorizerPropertyAttribute):
                    var bv = vectorizerp as BindVectorizerPropertyAttribute;
                    if (bv == null) throw new Exception("Vectorizer is not of type 'BindVectorizerPropertyAttribute'");
                    p.ModuleConfig = new Modules.Multi2Vec.BindPropertyModCfg()
                    {
                        Parameters = new Modules.Multi2Vec.BindPropertyModParams()
                        {
                            skip = bv.skip,
                            vectorizePropertyName = bv.vectorizePropertyName
                        }
                    };
                    break;
            }
        }

        public WeaviateClass<P>? GetClass<P>(string name) where P : class, new()
        {
            var c = this.Classes.FirstOrDefault(c => c.Name == name);
            if (c == null) return null;
            var ret = new WeaviateClass<P>(c);
            ret._connection = _connection; // Connection is *not* copied
            return ret;
        }

        public async Task<SchemaClusterStatus> ClusterStatus()
        {
            if (_connection == null) throw new Exception($"Empty connection while retrieving cluster status");
            return await _connection.Client.Schema_cluster_statusAsync();
        }

        public async Task<GraphQLResponse> RawQuery(GraphQLQuery query)
        {
            if (_connection == null) throw new Exception($"Empty connection while querying schema '{query}'");
            return await _connection.Client.Graphql_postAsync(query);
        }

        public async Task<ICollection<GraphQLResponse>> RawQueryBatch(ICollection<GraphQLQuery> queries)
        {
            if (_connection == null) throw new Exception($"Empty connection while querying schema '{queries}'");
            return await _connection.Client.Graphql_batchAsync(queries);
        }

        public async Task<BackupCreateResponse> BackupStart(string backend, BackupCreateRequest request)
        {
            if (_connection == null) throw new Exception($"Empty connection while starting backup on '{backend}'");
            return await _connection.Client.Backups_createAsync(backend, request);
        }

        public async Task<BackupCreateStatusResponse> BackupStatus(string backend, string id)
        {
            if (_connection == null) throw new Exception($"Empty connection while checking backup '{id}'");
            return await _connection.Client.Backups_create_statusAsync(backend, id);
        }

        public async Task<BackupRestoreResponse> RestoreBackup(string backend, string id, BackupRestoreRequest request)
        {
            if (_connection == null) throw new Exception($"Empty connection while restoring backup '{id}'");
            return await _connection.Client.Backups_restoreAsync(backend, id, request);
        }

        public async Task<BackupRestoreStatusResponse> RestoreBackupStatus(string backend, string id)
        {
            if (_connection == null) throw new Exception($"Empty connection while checking backup restore '{id}'");
            return await _connection.Client.Backups_restore_statusAsync(backend, id);
        }

        public async Task<NodesStatusResponse> CheckNodesStatus()
        {
            if (_connection == null) throw new Exception($"Empty connection while checking nodes status");
            return await _connection.Client.Nodes_getAsync();
        }
    }
}

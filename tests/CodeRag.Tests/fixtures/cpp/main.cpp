// Placeholder C++ — classes, inheritance, templates, overloads
template<typename T> class Repository { public: T get(int id); };
class IClientService { public: virtual void DeactivateClient(int id)=0; };
class Client { public: bool isActive; };
class ClientService : public IClientService { void DeactivateClient(int id) override {} };
